using System.Globalization;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

internal class Program
{
    private static readonly string DataFile = Path.Combine(AppContext.BaseDirectory, "users.json");
    private static readonly string BudgetFile = Path.Combine(AppContext.BaseDirectory, "budgets.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Dictionary<long, PendingOperation> Pending = new();
    private static readonly Dictionary<long, PendingEdit> PendingEdits = new();
    private static Dictionary<long, List<MoneyOperation>> Data = new();
    private static Dictionary<long, decimal> Budgets = new();
    private static readonly string[] ExpenseCategories = { "Еда", "Транспорт", "Жильё", "Связь", "Здоровье", "Покупки", "Развлечения", "Непредвиденные", "Другое" };
    private static readonly string[] IncomeCategories = { "Зарплата", "Подработка", "Подарок", "Продажа", "Другое" };

    private static async Task Main()
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("Не найдена переменная среды TELEGRAM_BOT_TOKEN.");
            Console.WriteLine("Создайте бота через @BotFather и добавьте токен в переменные среды Windows.");
            Console.ReadKey();
            return;
        }

        LoadData();
        var bot = new TelegramBotClient(token);
        using var cts = new CancellationTokenSource();
        var me = await bot.GetMe(cts.Token);
        Console.WriteLine($"FinanceBot (@{me.Username}) запущен. Для остановки нажмите Ctrl+C.");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        bot.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync,
            new Telegram.Bot.Polling.ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() }, cts.Token);
        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }
        SaveData();
    }

    private static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Type == UpdateType.Message && update.Message?.Text is string text)
            await HandleMessage(bot, update.Message, text.Trim(), ct);
        else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } callback)
        {
            await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            if (callback.Message is not null && callback.Data is not null)
                await HandleCallback(bot, callback.Message.Chat.Id, callback.Message.MessageId, callback.Data, ct);
        }
    }

    private static Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        Console.WriteLine($"Ошибка Telegram: {exception.Message}");
        return Task.CompletedTask;
    }

    private static async Task HandleMessage(ITelegramBotClient bot, Message message, string text, CancellationToken ct)
    {
        long id = message.Chat.Id;
        if (text == "/start" || text == "🏠 Главное меню")
        {
            Pending.Remove(id); PendingEdits.Remove(id);
            await ShowMainMenu(bot, id, ct); return;
        }
        if (text == "/help" || text == "❓ Помощь")
        {
            await bot.SendMessage(id,
                "📋 Команды FinanceBot\n\n" +
                "/start — открыть главное меню.\n" +
                "/income — добавить доход: введите сумму, выберите категорию и укажите описание.\n" +
                "/expense — добавить расход: введите сумму, выберите категорию и укажите описание.\n" +
                "/balance — показать текущий баланс, сумму доходов, расходов и корректировок.\n" +
                "/stats — показать доходы и расходы за текущий месяц, разницу и расходы по категориям.\n" +
                "/history — показать последние 10 операций; выберите операцию, чтобы изменить или удалить её.\n" +
                "/budget — установить месячный лимит расходов или посмотреть, сколько уже потрачено и осталось. Введите 0, чтобы убрать лимит.\n" +
                "/adjust — сверить баланс: введите фактическую сумму денег, и бот учтёт разницу.\n" +
                "/cancel — отменить текущий ввод.\n" +
                "/help — показать список команд.\n\n" +
                "Для работы выберите команду или кнопку в главном меню.", cancellationToken: ct);
            return;
        }
        if (text == "➕ Доход" || text == "/income") { StartOperation(id, "Доход"); await bot.SendMessage(id, "Введите фактически полученную сумму дохода в рублях (например, 42000 или 42000,50).", cancellationToken: ct); return; }
        if (text == "➖ Расход" || text == "/expense") { StartOperation(id, "Расход"); await bot.SendMessage(id, "Введите сумму расхода в рублях (например, 850 или 850,50).", cancellationToken: ct); return; }
        if (text == "💰 Баланс" || text == "/balance") { await ShowBalance(bot, id, ct); return; }
        if (text == "📊 Статистика" || text == "/stats") { await ShowStats(bot, id, ct); return; }
        if (text == "📜 История" || text == "/history") { await ShowHistory(bot, id, ct); return; }
        if (text == "🎯 Бюджет" || text == "/budget") { Pending[id] = new PendingOperation { Step = "budget" }; await bot.SendMessage(id, "Введите месячный лимит расходов в рублях. Например: 20000. Чтобы убрать лимит, отправьте 0.", cancellationToken: ct); return; }
        if (text == "🔄 Сверить баланс" || text == "/adjust") { Pending[id] = new PendingOperation { Step = "adjust" }; await bot.SendMessage(id, $"Сейчас по записям бота баланс: {GetBalance(id):N2} ₽.\nВведите, сколько денег у вас фактически сейчас. Бот добавит корректирующую запись на разницу.", cancellationToken: ct); return; }
        if (text == "❌ Отмена" || text == "/cancel") { Pending.Remove(id); PendingEdits.Remove(id); await bot.SendMessage(id, "Текущее действие отменено.", cancellationToken: ct); await ShowMainMenu(bot, id, ct); return; }

        if (PendingEdits.TryGetValue(id, out var edit))
        {
            await ProcessEditInput(bot, id, text, edit, ct); return;
        }
        if (Pending.TryGetValue(id, out var pending))
        {
            if (pending.Step == "budget")
            {
                if (!TryAmount(text, allowZero: true, out var amount)) { await bot.SendMessage(id, "Введите неотрицательное число, например 20000 или 0.", cancellationToken: ct); return; }
                if (amount == 0) Budgets.Remove(id); else Budgets[id] = amount;
                Pending.Remove(id); SaveBudgets();
                await bot.SendMessage(id, amount == 0 ? "Месячный лимит удалён." : $"Месячный лимит установлен: {amount:N2} ₽.", cancellationToken: ct);
                await ShowBudget(bot, id, ct); return;
            }
            if (pending.Step == "adjust")
            {
                if (!TryAmount(text, allowZero: true, out var actual)) { await bot.SendMessage(id, "Введите фактическую сумму от 0 и выше.", cancellationToken: ct); return; }
                var difference = actual - GetBalance(id);
                if (difference == 0) { Pending.Remove(id); await bot.SendMessage(id, "Баланс уже совпадает. Корректировка не требуется.", cancellationToken: ct); return; }
                GetOrCreateOperations(id).Add(new MoneyOperation { Type = "Корректировка", Amount = difference, Category = "Сверка баланса", Description = "Ручная сверка с фактическим остатком", Date = DateTime.Now });
                Pending.Remove(id); SaveData();
                await bot.SendMessage(id, $"Баланс скорректирован на {(difference > 0 ? "+" : "")}{difference:N2} ₽.\nТекущий баланс: {GetBalance(id):N2} ₽.", cancellationToken: ct); return;
            }
            if (pending.Step == "amount")
            {
                if (!TryAmount(text, allowZero: false, out var amount)) { await bot.SendMessage(id, "Не удалось распознать сумму. Введите положительное число, например 1200 или 1200,50.", cancellationToken: ct); return; }
                pending.Amount = amount; pending.Step = "category";
                var categories = pending.Type == "Доход" ? IncomeCategories : ExpenseCategories;
                var rows = categories.Select(c => new[] { InlineKeyboardButton.WithCallbackData(c, $"cat|{c}") }).ToArray();
                await bot.SendMessage(id, $"Сумма: {amount:N2} ₽\nВыберите категорию:", replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct); return;
            }
            if (pending.Step == "description")
            {
                pending.Description = text == "—" ? "" : text;
                SavePendingOperation(bot, id, pending, ct); return;
            }
        }
        await bot.SendMessage(id, "Выберите действие в меню или отправьте /help. Для отмены текущего ввода используйте /cancel.", cancellationToken: ct);
    }

    private static void StartOperation(long id, string type)
    {
        PendingEdits.Remove(id);
        Pending[id] = new PendingOperation { Type = type, Step = "amount" };
    }

    private static async Task HandleCallback(ITelegramBotClient bot, long id, int messageId, string data, CancellationToken ct)
    {
        if (data.StartsWith("cat|", StringComparison.Ordinal))
        {
            if (!Pending.TryGetValue(id, out var pending) || pending.Step != "category") return;
            var category = data[4..];
            if (!(pending.Type == "Доход" ? IncomeCategories : ExpenseCategories).Contains(category)) return;
            pending.Category = category; pending.Step = "description";
            await bot.SendMessage(id, "Добавьте описание операции или отправьте символ — чтобы пропустить. Например: «Зарплата за ноябрь».", cancellationToken: ct); return;
        }
        if (data == "skipdesc")
        {
            if (Pending.TryGetValue(id, out var p) && p.Step == "description") { p.Description = ""; await SavePendingOperation(bot, id, p, ct); }
            return;
        }
        // Для операций используем постоянный GUID, а не индекс списка.
        // Индексы меняются после удаления и могут устареть в старых сообщениях истории.
        if (data.StartsWith("op|", StringComparison.Ordinal) && Guid.TryParse(data[3..], out var selectedId))
        {
            var list = GetOrCreateOperations(id);
            var op = list.FirstOrDefault(x => x.Id == selectedId);
            if (op is null) { await bot.SendMessage(id, "Эта операция уже не существует. Откройте историю заново.", cancellationToken: ct); return; }
            var key = selectedId.ToString("N");
            var buttons = new InlineKeyboardMarkup(new[] {
                new[] { InlineKeyboardButton.WithCallbackData("Изменить сумму", $"ea|{key}"), InlineKeyboardButton.WithCallbackData("Категорию", $"ec|{key}") },
                new[] { InlineKeyboardButton.WithCallbackData("Описание", $"ed|{key}"), InlineKeyboardButton.WithCallbackData("Удалить", $"del|{key}") }
            });
            await bot.SendMessage(id, $"Операция: {(op.Type == "Корректировка" ? "Корректировка баланса" : op.Type)}\nСумма: {op.Amount:N2} ₽\nКатегория: {op.Category}\nОписание: {(string.IsNullOrWhiteSpace(op.Description) ? "—" : op.Description)}\nДата: {op.Date:dd.MM.yyyy HH:mm}", replyMarkup: buttons, cancellationToken: ct); return;
        }
        if (data.StartsWith("ea|", StringComparison.Ordinal) && Guid.TryParse(data[3..], out var amountId))
        { await BeginEdit(bot, id, amountId, "amount", "Введите новую положительную сумму:", ct); return; }
        if (data.StartsWith("ed|", StringComparison.Ordinal) && Guid.TryParse(data[3..], out var descId))
        { await BeginEdit(bot, id, descId, "description", "Введите новое описание. Чтобы очистить описание, отправьте —:", ct); return; }
        if (data.StartsWith("ec|", StringComparison.Ordinal) && Guid.TryParse(data[3..], out var catId))
        {
            var list = GetOrCreateOperations(id); var op = list.FirstOrDefault(x => x.Id == catId);
            if (op is null) { await bot.SendMessage(id, "Эта операция уже не существует. Откройте историю заново.", cancellationToken: ct); return; }
            var cats = op.Type == "Доход" ? IncomeCategories : op.Type == "Расход" ? ExpenseCategories : new[] { "Сверка баланса" };
            var rows = cats.Select(c => new[] { InlineKeyboardButton.WithCallbackData(c, $"setcat|{catId:N}|{c}") }).ToArray();
            await bot.SendMessage(id, "Выберите новую категорию:", replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct); return;
        }
        if (data.StartsWith("setcat|", StringComparison.Ordinal))
        {
            var parts = data.Split('|', 3); if (parts.Length != 3 || !Guid.TryParse(parts[1], out var categoryId)) return;
            var list = GetOrCreateOperations(id); var op = list.FirstOrDefault(x => x.Id == categoryId);
            if (op is null) { await bot.SendMessage(id, "Эта операция уже не существует. Откройте историю заново.", cancellationToken: ct); return; }
            op.Category = parts[2]; SaveData(); await bot.SendMessage(id, "Категория изменена.", cancellationToken: ct); return;
        }
        if (data.StartsWith("del|", StringComparison.Ordinal) && Guid.TryParse(data[4..], out var deleteId))
        {
            var list = GetOrCreateOperations(id); var op = list.FirstOrDefault(x => x.Id == deleteId);
            if (op is null) { await bot.SendMessage(id, "Эта операция уже не существует. Откройте историю заново.", cancellationToken: ct); return; }
            await bot.SendMessage(id, $"Удалить операцию «{op.Category}» на {op.Amount:N2} ₽?", replyMarkup: new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("Да, удалить", $"yesdel|{deleteId:N}"), InlineKeyboardButton.WithCallbackData("Отмена", "canceldelete") } }), cancellationToken: ct); return;
        }
        if (data.StartsWith("yesdel|", StringComparison.Ordinal) && Guid.TryParse(data[7..], out var confirmedId))
        {
            var list = GetOrCreateOperations(id); var index = list.FindIndex(x => x.Id == confirmedId);
            if (index < 0) { await bot.SendMessage(id, "Операция уже удалена или не найдена. Обновите историю командой /history.", cancellationToken: ct); return; }
            list.RemoveAt(index);
            SaveData();
            await bot.SendMessage(id, "Операция удалена. Баланс пересчитан.", cancellationToken: ct);
            await ShowBalance(bot, id, ct);
            return;
        }
        if (data == "canceldelete") await bot.SendMessage(id, "Удаление отменено.", cancellationToken: ct);
    }

    private static async Task BeginEdit(ITelegramBotClient bot, long id, Guid operationId, string field, string prompt, CancellationToken ct)
    {
        var list = GetOrCreateOperations(id);
        if (!list.Any(x => x.Id == operationId)) { await bot.SendMessage(id, "Операция уже не найдена. Откройте историю заново.", cancellationToken: ct); return; }
        PendingEdits[id] = new PendingEdit { OperationId = operationId, Field = field };
        await bot.SendMessage(id, prompt + " Для отмены отправьте /cancel.", cancellationToken: ct);
    }

    private static async Task ProcessEditInput(ITelegramBotClient bot, long id, string text, PendingEdit edit, CancellationToken ct)
    {
        var list = GetOrCreateOperations(id);
        var operation = list.FirstOrDefault(x => x.Id == edit.OperationId);
        if (operation is null) { PendingEdits.Remove(id); await bot.SendMessage(id, "Операция не найдена.", cancellationToken: ct); return; }
        if (edit.Field == "amount")
        {
            if (!TryAmount(text, false, out var amount)) { await bot.SendMessage(id, "Введите положительную сумму, например 1500,50.", cancellationToken: ct); return; }
            operation.Amount = amount;
        }
        else operation.Description = text == "—" ? "" : text;
        PendingEdits.Remove(id); SaveData();
        await bot.SendMessage(id, "Операция обновлена. Баланс пересчитан автоматически.", cancellationToken: ct);
    }

    private static async Task SavePendingOperation(ITelegramBotClient bot, long id, PendingOperation pending, CancellationToken ct)
    {
        GetOrCreateOperations(id).Add(new MoneyOperation { Type = pending.Type, Amount = pending.Amount, Category = pending.Category ?? "Другое", Description = pending.Description ?? "", Date = DateTime.Now });
        Pending.Remove(id); SaveData();
        await bot.SendMessage(id, "Операция сохранена. Баланс пересчитан.", replyMarkup: new ReplyKeyboardMarkup(new[] { new[] { new KeyboardButton("🏠 Главное меню") } }) { ResizeKeyboard = true }, cancellationToken: ct);
        await ShowBalance(bot, id, ct);
    }

    private static async Task ShowMainMenu(ITelegramBotClient bot, long id, CancellationToken ct)
    {
        var keyboard = new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("➕ Доход"), new KeyboardButton("➖ Расход") },
            new[] { new KeyboardButton("💰 Баланс"), new KeyboardButton("📊 Статистика") },
            new[] { new KeyboardButton("📜 История"), new KeyboardButton("🎯 Бюджет") },
            new[] { new KeyboardButton("🔄 Сверить баланс"), new KeyboardButton("❓ Помощь") }
        }) { ResizeKeyboard = true };
        await bot.SendMessage(id, "FinanceBot — учёт личных финансов.\n\nДобавляйте реальные доходы и расходы, контролируйте баланс и месячный бюджет. Выберите действие:", replyMarkup: keyboard, cancellationToken: ct);
    }

    private static async Task ShowBalance(ITelegramBotClient bot, long id, CancellationToken ct)
    {
        var ops = GetOperations(id);
        var income = ops.Where(x => x.Type == "Доход").Sum(x => x.Amount);
        var expense = ops.Where(x => x.Type == "Расход").Sum(x => x.Amount);
        var corrections = ops.Where(x => x.Type == "Корректировка").Sum(x => x.Amount);
        await bot.SendMessage(id, $"💰 Баланс: {GetBalance(id):N2} ₽\n\nДоходы: +{income:N2} ₽\nРасходы: −{expense:N2} ₽\nКорректировки: {corrections:+0.00;-0.00;0.00} ₽", cancellationToken: ct);
    }

    private static async Task ShowStats(ITelegramBotClient bot, long id, CancellationToken ct)
    {
        var ops = GetOperations(id).Where(x => x.Date.Year == DateTime.Now.Year && x.Date.Month == DateTime.Now.Month).ToList();
        var income = ops.Where(x => x.Type == "Доход").Sum(x => x.Amount);
        var expense = ops.Where(x => x.Type == "Расход").Sum(x => x.Amount);
        var byCategory = ops.Where(x => x.Type == "Расход").GroupBy(x => x.Category).OrderByDescending(g => g.Sum(x => x.Amount)).ToList();
        var breakdown = byCategory.Count == 0 ? "Расходов пока нет." : string.Join("\n", byCategory.Select(g => $"• {g.Key}: {g.Sum(x => x.Amount):N2} ₽"));
        await bot.SendMessage(id, $"📊 Статистика за {DateTime.Now:MM.yyyy}\n\nДоходы: +{income:N2} ₽\nРасходы: −{expense:N2} ₽\nРазница: {income - expense:N2} ₽\n\nРасходы по категориям:\n{breakdown}", cancellationToken: ct);
        if (Budgets.ContainsKey(id)) await ShowBudget(bot, id, ct);
    }

    private static async Task ShowBudget(ITelegramBotClient bot, long id, CancellationToken ct)
    {
        if (!Budgets.TryGetValue(id, out var limit)) { await bot.SendMessage(id, "Месячный бюджет не установлен. Нажмите «🎯 Бюджет», чтобы задать лимит расходов.", cancellationToken: ct); return; }
        var spent = GetOperations(id).Where(x => x.Type == "Расход" && x.Date.Year == DateTime.Now.Year && x.Date.Month == DateTime.Now.Month).Sum(x => x.Amount);
        var remaining = limit - spent;
        await bot.SendMessage(id, $"🎯 Бюджет на {DateTime.Now:MM.yyyy}\nЛимит: {limit:N2} ₽\nПотрачено: {spent:N2} ₽\n{(remaining >= 0 ? "Осталось" : "Превышение")}: {Math.Abs(remaining):N2} ₽", cancellationToken: ct);
    }

    private static async Task ShowHistory(ITelegramBotClient bot, long id, CancellationToken ct)
    {
        var list = GetOrCreateOperations(id);
        var items = list.Select((op, index) => new { op, index }).OrderByDescending(x => x.op.Date).Take(10).ToList();
        if (items.Count == 0) { await bot.SendMessage(id, "История пока пуста. Добавьте первую операцию через меню.", cancellationToken: ct); return; }
        var lines = items.Select(x => $"{(x.op.Type == "Доход" ? "+" : x.op.Type == "Расход" ? "−" : "↔")}{x.op.Amount:N2} ₽ | {x.op.Category} | {x.op.Date:dd.MM HH:mm}\n{x.op.Description}");
        var buttons = items.Select(x => new[] { InlineKeyboardButton.WithCallbackData($"Операция {x.op.Date:dd.MM HH:mm} — {x.op.Amount:N0} ₽", $"op|{x.op.Id:N}") }).ToArray();
        await bot.SendMessage(id, "📜 Последние 10 операций (нажмите на операцию, чтобы изменить или удалить):\n\n" + string.Join("\n\n", lines), replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: ct);
    }

    private static decimal GetBalance(long id) => GetOperations(id).Sum(x => x.Type == "Доход" ? x.Amount : x.Type == "Расход" ? -x.Amount : x.Amount);
    private static List<MoneyOperation> GetOperations(long id) => Data.TryGetValue(id, out var list) ? list : new List<MoneyOperation>();
    private static List<MoneyOperation> GetOrCreateOperations(long id) { if (!Data.TryGetValue(id, out var list)) Data[id] = list = new(); return list; }

    private static bool TryAmount(string text, bool allowZero, out decimal amount)
    {
        var normalized = text.Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out amount) && amount >= (allowZero ? 0 : 0.01m) && amount <= 1_000_000_000m;
    }

    private static void LoadData()
    {
        try
        {
            if (File.Exists(DataFile))
            {
                Data = JsonSerializer.Deserialize<Dictionary<long, List<MoneyOperation>>>(File.ReadAllText(DataFile)) ?? new();
                // Совместимость со старыми users.json, в которых ещё не было ID операций.
                var migrated = false;
                foreach (var operation in Data.Values.SelectMany(x => x))
                {
                    if (operation.Id != Guid.Empty) continue;
                    operation.Id = Guid.NewGuid();
                    migrated = true;
                }
                if (migrated) SaveData();
            }
        }
        catch (Exception ex) { Console.WriteLine($"Не удалось прочитать users.json: {ex.Message}"); Data = new(); }
        try { if (File.Exists(BudgetFile)) Budgets = JsonSerializer.Deserialize<Dictionary<long, decimal>>(File.ReadAllText(BudgetFile)) ?? new(); }
        catch (Exception ex) { Console.WriteLine($"Не удалось прочитать budgets.json: {ex.Message}"); Budgets = new(); }
    }
    private static void SaveData() { try { File.WriteAllText(DataFile, JsonSerializer.Serialize(Data, JsonOptions)); } catch (Exception ex) { Console.WriteLine($"Ошибка сохранения users.json: {ex.Message}"); } }
    private static void SaveBudgets() { try { File.WriteAllText(BudgetFile, JsonSerializer.Serialize(Budgets, JsonOptions)); } catch (Exception ex) { Console.WriteLine($"Ошибка сохранения budgets.json: {ex.Message}"); } }

    private sealed class PendingOperation
    {
        public string Type { get; set; } = "";
        public string Step { get; set; } = "";
        public decimal Amount { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
    }
    private sealed class PendingEdit { public Guid OperationId { get; set; } public string Field { get; set; } = ""; }
    private sealed class MoneyOperation
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Type { get; set; } = "";
        public decimal Amount { get; set; }
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime Date { get; set; }
    }
}
