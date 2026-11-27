namespace FootballTickets;

internal static class ConsoleMenu
{
    public static bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected
        && Environment.GetEnvironmentVariable("TERM") != "dumb";

    public static string? Select(
        string title, IReadOnlyList<(string Key, string Label)> options, string? subtitle = null,
        string prompt = "Действие", Func<string>? liveStatus = null)
    {
        if (!Interactive)
        {
            ShowTitle(title, subtitle);
            foreach (var (key, label) in options)
            {
                Console.WriteLine($"  [{key}]  {label}");
            }
            Console.Write($"  {prompt}: ");
            return Console.ReadLine()?.Trim();
        }

        var selected = 0;
        var actionWidth = options.Max(option => option.Label.Length);
        while (true)
        {
            Console.Clear();
            ShowTitle(title, subtitle);

            for (var index = 0; index < options.Count; index++)
            {
                var (key, label) = options[index];
                var active = index == selected;
                Console.Write("  ");
                Paint($" {(active ? "›" : " ")}   {key}   {label.PadRight(actionWidth)}  ",
                    active ? ConsoleColor.Black : ConsoleColor.Gray,
                    active ? ConsoleColor.Cyan : null);
                Console.WriteLine();
            }

            Console.WriteLine();
            Paint($"  ↑/↓  Выбор    Enter  Открыть    Esc  {options[^1].Label}", ConsoleColor.Gray);
            Console.WriteLine();

            while (true)
            {
                var key = ReadKeyWithStatus(liveStatus);
                if (key.Key == ConsoleKey.Enter)
                {
                    if (liveStatus is not null)
                    {
                        Console.WriteLine();
                    }
                    return options[selected].Key;
                }
                if (key.Key == ConsoleKey.Escape)
                {
                    if (liveStatus is not null)
                    {
                        Console.WriteLine();
                    }
                    return "0";
                }

                var next = key.Key switch
                {
                    ConsoleKey.UpArrow => (selected + options.Count - 1) % options.Count,
                    ConsoleKey.DownArrow or ConsoleKey.Tab => (selected + 1) % options.Count,
                    ConsoleKey.Home => 0,
                    ConsoleKey.End => options.Count - 1,
                    _ => FindOption(options, key.KeyChar, selected)
                };
                if (next != selected)
                {
                    selected = next;
                    break;
                }
            }
        }
    }

    public static bool Confirm(string prompt, DateTimeOffset? deadline = null)
    {
        if (!Interactive)
        {
            return ReadConfirmation(prompt);
        }

        Console.WriteLine();
        Console.WriteLine($"  {prompt}?");
        Console.WriteLine("  ←/→  Выбор    Enter  Подтвердить    Esc  Отмена");
        var yes = false;
        var yesLabel = deadline.HasValue ? "Оплатить" : "Да";
        var noLabel = deadline.HasValue ? "Отмена" : "Нет";
        var previousSeconds = -1;
        var redraw = true;

        while (true)
        {
            var seconds = 0;
            if (deadline is { } endsAt)
            {
                var remaining = endsAt - DateTimeOffset.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    DrawConfirmation(yesLabel, noLabel, yes: false, 0);
                    Console.WriteLine();
                    throw new TicketOfficeException("Время оплаты вышло. Оформление отменено.");
                }
                seconds = (int)Math.Ceiling(remaining.TotalSeconds);
            }

            if (redraw || previousSeconds != seconds)
            {
                DrawConfirmation(yesLabel, noLabel, yes, deadline.HasValue ? seconds : null);
                previousSeconds = seconds;
                redraw = false;
            }

            if (deadline.HasValue && !Console.KeyAvailable)
            {
                Thread.Sleep(75);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.LeftArrow:
                    yes = true;
                    break;
                case ConsoleKey.RightArrow:
                    yes = false;
                    break;
                case ConsoleKey.Tab or ConsoleKey.UpArrow or ConsoleKey.DownArrow:
                    yes = !yes;
                    break;
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return yes;
                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return false;
                default:
                    switch (char.ToLowerInvariant(key.KeyChar))
                    {
                        case 'д' or 'y':
                            Console.WriteLine();
                            return true;
                        case 'н' or 'n' or '0':
                            Console.WriteLine();
                            return false;
                    }
                    break;
            }
            redraw = true;
        }
    }

    public static IReadOnlyList<T>? SelectItems<T>(string title, IReadOnlyList<T> items, Func<T, string> label,
        string subtitle, Func<IReadOnlyList<T>, string>? summarizeSelection = null, Func<T, string>? liveStatus = null)
    {
        var multiple = summarizeSelection is not null;
        var marked = new HashSet<int>();
        var focused = 0;
        var emptySelection = false;

        while (true)
        {
            Console.Clear();
            ShowTitle(title, subtitle);

            var width = Math.Clamp(Console.WindowWidth - 8, 20, 88);
            var pageSize = Math.Clamp(Console.WindowHeight - 12, 3, 12);
            var start = focused / pageSize * pageSize;
            var end = Math.Min(start + pageSize, items.Count);

            for (var index = start; index < end; index++)
            {
                var active = index == focused;
                var marker = multiple ? marked.Contains(index) ? "✓ " : "○ " : "";
                var text = label(items[index]);
                if (text.Length > width)
                {
                    text = text[..(width - 1)] + "…";
                }
                Console.Write("  ");
                Paint($"{(active ? "›" : " ")} {marker}{text.PadRight(width)}",
                    active ? ConsoleColor.Black : marked.Contains(index) ? ConsoleColor.Green : ConsoleColor.Gray,
                    active ? ConsoleColor.Cyan : null);
                Console.WriteLine();
            }

            var selection = marked.Order().Select(index => items[index]).ToArray();
            Console.WriteLine();
            if (items.Count > pageSize)
            {
                Console.WriteLine($"  Записи {start + 1}–{end} из {items.Count}");
            }
            if (summarizeSelection is not null)
            {
                Paint($"  {summarizeSelection(selection)}", ConsoleColor.Cyan);
                Console.WriteLine();
            }
            Console.WriteLine(multiple
                ? "  ↑/↓  Выбор    Пробел  Отметить    Enter  Дальше    Esc  Назад"
                : "  ↑/↓  Выбор    Enter  Дальше    Esc  Назад");
            if (emptySelection)
            {
                Paint("  Отметь хотя бы один билет.", ConsoleColor.Yellow);
                Console.WriteLine();
            }

            var key = ReadKeyWithStatus(liveStatus is null ? null : () => liveStatus(items[focused])).Key;
            emptySelection = false;
            switch (key)
            {
                case ConsoleKey.Escape:
                    return null;
                case ConsoleKey.Enter when !multiple:
                    return [items[focused]];
                case ConsoleKey.Enter when selection.Length > 0:
                    return selection;
                case ConsoleKey.Enter:
                    emptySelection = true;
                    break;
                case ConsoleKey.Spacebar when multiple:
                    if (!marked.Add(focused))
                    {
                        marked.Remove(focused);
                    }
                    break;
                case ConsoleKey.UpArrow:
                    focused = (focused + items.Count - 1) % items.Count;
                    break;
                case ConsoleKey.DownArrow or ConsoleKey.Tab:
                    focused = (focused + 1) % items.Count;
                    break;
                case ConsoleKey.Home:
                    focused = 0;
                    break;
                case ConsoleKey.End:
                    focused = items.Count - 1;
                    break;
                case ConsoleKey.PageUp:
                    focused = Math.Max(0, focused - pageSize);
                    break;
                case ConsoleKey.PageDown:
                    focused = Math.Min(items.Count - 1, focused + pageSize);
                    break;
            }
        }
    }

    public static void Pause(Func<string>? liveStatus = null)
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  Enter / Esc — вернуться в меню");
        if (!Interactive)
        {
            Console.ReadLine();
            return;
        }
        while (ReadKeyWithStatus(liveStatus).Key is not (ConsoleKey.Enter or ConsoleKey.Escape))
        {
        }
    }

    private static ConsoleKeyInfo ReadKeyWithStatus(Func<string>? liveStatus)
    {
        if (liveStatus is null)
        {
            return Console.ReadKey(intercept: true);
        }

        string? displayed = null;
        while (true)
        {
            var width = Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 79;
            var status = $"  {liveStatus()}";
            if (status.Length > width)
            {
                status = width > 0 ? status[..(width - 1)] + "…" : "";
            }
            if (displayed != status)
            {
                Console.Write('\r');
                Paint(status.PadRight(width), ConsoleColor.Cyan);
                displayed = status;
            }
            if (Console.KeyAvailable)
            {
                return Console.ReadKey(intercept: true);
            }
            Thread.Sleep(100);
        }
    }

    public static void ShowTitle(string title, string? subtitle = null)
    {
        var rule = new string('─', title.Length + 4);
        Console.WriteLine();
        Paint($"  ┌{rule}┐", ConsoleColor.Cyan);
        Console.WriteLine();
        Paint($"  │  {title}  │", ConsoleColor.Cyan);
        Console.WriteLine();
        Paint($"  └{rule}┘", ConsoleColor.Cyan);
        Console.WriteLine();
        if (subtitle is not null)
        {
            Console.WriteLine($"  {subtitle}");
        }
        Console.WriteLine();
    }

    public static void Paint(string text, ConsoleColor foreground, ConsoleColor? background = null)
    {
        if (!Interactive)
        {
            Console.Write(text);
            return;
        }

        var previousForeground = Console.ForegroundColor;
        var previousBackground = Console.BackgroundColor;
        Console.ForegroundColor = foreground;
        if (background is { } color)
        {
            Console.BackgroundColor = color;
        }
        Console.Write(text);
        Console.BackgroundColor = previousBackground;
        Console.ForegroundColor = previousForeground;
    }

    private static int FindOption(IReadOnlyList<(string Key, string Label)> options, char key, int fallback)
    {
        for (var index = 0; index < options.Count; index++)
        {
            if (options[index].Key.Length == 1 && options[index].Key[0] == key)
            {
                return index;
            }
        }
        return fallback;
    }

    private static void DrawConfirmation(string yesLabel, string noLabel, bool yes, int? seconds)
    {
        Console.Write("\r  ");
        if (seconds is int time)
        {
            Paint($"  ОСТАЛОСЬ {time / 60:00}:{time % 60:00}  ", ConsoleColor.Black,
                time <= 60 ? ConsoleColor.Yellow : ConsoleColor.Cyan);
            Console.Write("  ");
        }
        DrawButton(yesLabel, yes);
        Console.Write("  ");
        DrawButton(noLabel, !yes);
    }

    private static void DrawButton(string label, bool active) =>
        Paint($"[ {label} ]", active ? ConsoleColor.Black : ConsoleColor.White,
            active ? ConsoleColor.Cyan : ConsoleColor.DarkGray);

    private static bool ReadConfirmation(string prompt)
    {
        while (true)
        {
            Console.Write($"  {prompt}? [д/н]: ");
            switch (Console.ReadLine()?.Trim().ToLowerInvariant())
            {
                case "д" or "да" or "y" or "yes":
                    return true;
                case null or "" or "н" or "нет" or "n" or "no" or "0":
                    return false;
                default:
                    Console.WriteLine("  Введи «д» или «н».");
                    break;
            }
        }
    }
}
