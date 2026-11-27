namespace FootballTickets;

internal static class SeatPicker
{
    public static int[]? Select(TicketOffice office, IReadOnlyList<Match> matches, bool multiple,
        Func<int[], DateTimeOffset>? applySelection = null)
    {
        var rows = office.Seats.GroupBy(seat => seat.Row).Select(group => group.ToArray()).ToArray();
        var selected = new HashSet<int>();
        var row = 0;
        var column = 0;
        var message = "";
        DateTimeOffset? deadline = null;

        while (true)
        {
            Console.Clear();
            Draw(office, matches, rows, selected, row, column, message);
            var key = ReadKeyUntil(deadline);
            var focusedSeat = rows[row][column];
            message = "";

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    return null;
                case ConsoleKey.LeftArrow:
                    column = (column + rows[row].Length - 1) % rows[row].Length;
                    break;
                case ConsoleKey.RightArrow:
                    column = (column + 1) % rows[row].Length;
                    break;
                case ConsoleKey.UpArrow:
                    row = (row + rows.Length - 1) % rows.Length;
                    column = Math.Min(column, rows[row].Length - 1);
                    break;
                case ConsoleKey.DownArrow:
                    row = (row + 1) % rows.Length;
                    column = Math.Min(column, rows[row].Length - 1);
                    break;
                case ConsoleKey.Enter:
                    if (selected.Count > 0)
                    {
                        return selected.Order().ToArray();
                    }
                    message = "Отметь хотя бы одно место пробелом.";
                    break;
                case ConsoleKey.Spacebar:
                    var next = new HashSet<int>(selected);
                    if (!next.Remove(focusedSeat.Number))
                    {
                        if (Availability(office, matches, focusedSeat, DateTimeOffset.Now) != SeatAvailability.Free)
                        {
                            message = $"Место №{focusedSeat.Number} недоступно.";
                            break;
                        }
                        if (!multiple)
                        {
                            next.Clear();
                        }
                        next.Add(focusedSeat.Number);
                    }

                    try
                    {
                        if (applySelection is not null)
                        {
                            deadline = applySelection(next.Order().ToArray());
                        }
                        selected = next;
                    }
                    catch (TicketOfficeException error)
                    {
                        message = error.Message;
                    }
                    break;
            }
        }
    }

    private static void Draw(TicketOffice office, IReadOnlyList<Match> matches, Seat[][] rows,
        HashSet<int> selected, int focusedRow, int focusedColumn, string message)
    {
        var time = DateTimeOffset.Now;
        ConsoleMenu.ShowTitle("СХЕМА ЗАЛА", matches.Count == 1 ? null : "Абонемент · одно место на всех матчах пакета");
        foreach (var match in matches)
        {
            Console.WriteLine($"  {match.StartsAt:dd.MM HH:mm}   {match.Name}");
        }

        Console.WriteLine();
        ConsoleMenu.Paint("  ┌─────────────────────  ФУТБОЛЬНОЕ ПОЛЕ  ─────────────────────┐", ConsoleColor.Green);
        Console.WriteLine();
        ConsoleMenu.Paint("  └─────────────────────────────────────────────────────────────┘", ConsoleColor.Green);
        Console.WriteLine();
        ConsoleMenu.Paint("                 СЕКТОР А                    СЕКТОР Б", ConsoleColor.Gray);
        Console.WriteLine();

        for (var row = 0; row < rows.Length; row++)
        {
            Console.Write($"  Ряд {rows[row][0].Row}  ");
            for (var column = 0; column < rows[row].Length; column++)
            {
                var seat = rows[row][column];
                if (column > 0 && rows[row][column - 1].Sector != seat.Sector)
                {
                    ConsoleMenu.Paint("│  ", ConsoleColor.Gray);
                }
                var chosen = selected.Contains(seat.Number);
                var (symbol, color) = chosen
                    ? ('+', ConsoleColor.Cyan)
                    : Availability(office, matches, seat, time) switch
                    {
                        SeatAvailability.Free => ('·', ConsoleColor.Green),
                        SeatAvailability.Reserved => ('Б', ConsoleColor.Yellow),
                        SeatAvailability.Held => ('~', ConsoleColor.Yellow),
                        _ => ('×', ConsoleColor.Gray)
                    };
                var focused = row == focusedRow && column == focusedColumn;
                ConsoleMenu.Paint($" {seat.Number:00}{symbol} ", focused ? ConsoleColor.Black : color,
                    focused ? ConsoleColor.Cyan : ConsoleColor.DarkGray);
                Console.Write("  ");
            }
            Console.WriteLine();
        }

        var focusedSeat = rows[focusedRow][focusedColumn];
        var total = office.Seats.Where(seat => selected.Contains(seat.Number)).Sum(seat => seat.Price) * matches.Count;
        Console.WriteLine();
        Console.WriteLine("  · свободно   + выбрано   Б бронь   ~ удержано   × продано");
        Console.WriteLine($"  Место №{focusedSeat.Number} · сектор {focusedSeat.Sector}, ряд {focusedSeat.Row} · {focusedSeat.Price * matches.Count:0.##} ₽");
        ConsoleMenu.Paint($"  Выбрано: {selected.Count}   Сумма: {total:0.##} ₽", ConsoleColor.Cyan);
        Console.WriteLine();
        Console.WriteLine("  Стрелки — выбор   Пробел — отметить   Enter — дальше   Esc — назад");
        if (message.Length > 0)
        {
            ConsoleMenu.Paint($"  !  {message}", ConsoleColor.Yellow);
            Console.WriteLine();
        }
    }

    private static ConsoleKeyInfo ReadKeyUntil(DateTimeOffset? deadline)
    {
        if (deadline is not { } endsAt)
        {
            return Console.ReadKey(intercept: true);
        }

        var displayedSeconds = -1;
        while (true)
        {
            var remaining = endsAt - DateTimeOffset.Now;
            var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            if (seconds != displayedSeconds)
            {
                Console.Write("\r  ");
                ConsoleMenu.Paint($"  ОСТАЛОСЬ {seconds / 60:00}:{seconds % 60:00}  ",
                    ConsoleColor.Black, seconds <= 60 ? ConsoleColor.Yellow : ConsoleColor.Cyan);
                displayedSeconds = seconds;
            }
            if (remaining <= TimeSpan.Zero)
            {
                Console.WriteLine();
                throw new TicketOfficeException("Время оплаты вышло. Оформление отменено.");
            }
            if (Console.KeyAvailable)
            {
                Console.WriteLine();
                return Console.ReadKey(intercept: true);
            }
            Thread.Sleep(75);
        }
    }

    private static SeatAvailability Availability(TicketOffice office, IReadOnlyList<Match> matches, Seat seat, DateTimeOffset time) =>
        matches.Max(match => office.GetAvailability(new MatchSeat(match, seat), time));
}
