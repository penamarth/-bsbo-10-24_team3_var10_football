using System.Globalization;
using System.Text;
using FootballTickets;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");

var office = new TicketOffice(DateTimeOffset.Now);
(string Key, string Label)[] mainMenu =
[
    ("1", "Купить билеты"),
    ("2", "Вернуть билеты"),
    ("3", "Купить абонемент"),
    ("4", "Вернуть абонемент"),
    ("5", "Управлять бронированием"),
    ("0", "Выйти")
];

while (true)
{
    var choice = ConsoleMenu.Select("БИЛЕТНАЯ КАССА", mainMenu, "Футбол · Прототип", liveStatus: ActiveReservationsStatus);
    if (choice is null or "0")
    {
        return;
    }

    try
    {
        switch (choice)
        {
            case "1":
                SellTickets();
                break;
            case "2":
                Refund(subscription: false);
                break;
            case "3":
                SellSubscription();
                break;
            case "4":
                Refund(subscription: true);
                break;
            case "5":
                ManageReservation();
                break;
            default:
                throw new TicketOfficeException("Выбери пункт от 0 до 5.");
        }
    }
    catch (TicketOfficeException error)
    {
        WriteLine($"!  {error.Message}", ConsoleColor.Yellow);
        ConsoleMenu.Pause();
    }
}

void SellTickets()
{
    var choice = ConsoleMenu.Select("ПОКУПКА БИЛЕТОВ",
        [("1", "Выбрать места на матч"), ("2", "Выкупить бронь"), ("0", "Назад")],
        liveStatus: ActiveReservationsStatus);

    switch (choice)
    {
        case "1":
            var match = ChooseMatch();
            if (match is null)
            {
                return;
            }
            ChooseTickets(match);
            break;
        case "2":
            RedeemReservation();
            break;
        case null or "0":
            break;
        default:
            throw new TicketOfficeException("Выбери 1 или 2.");
    }
}

void RedeemReservation()
{
    var reservationId = ChooseReservation("ВЫКУП БРОНИ");
    if (reservationId is int id)
    {
        Checkout(office.StartReservationSale(id, DateTimeOffset.Now));
    }
}

void ChooseTickets(Match match)
{
    if (!ConsoleMenu.Interactive)
    {
        ShowSeats([match]);
        var seats = ReadIds("Номера мест через запятую (0 — назад)");
        if (seats is not null)
        {
            Checkout(office.StartSale(match.Id, seats, DateTimeOffset.Now));
        }
        return;
    }

    Sale? sale = null;
    try
    {
        var seats = SeatPicker.Select(office, [match], multiple: true, applySelection: ids =>
        {
            var time = DateTimeOffset.Now;
            sale = sale is null ? office.StartSale(match.Id, ids, time) : office.UpdateSaleSelection(sale, ids, time);
            return sale.ExpiresAt;
        });
        if (seats is not null && sale is not null)
        {
            Checkout(sale);
        }
    }
    finally
    {
        if (sale is not null)
        {
            office.CancelSale(sale);
        }
    }
}

void SellSubscription()
{
    var matches = office.SubscriptionMatches;
    int? seatId;
    if (ConsoleMenu.Interactive)
    {
        seatId = SeatPicker.Select(office, matches, multiple: false)?[0];
    }
    else
    {
        Header("ПОКУПКА АБОНЕМЕНТА", "Одно и то же место на всех матчах пакета");
        foreach (var match in matches)
        {
            Console.WriteLine($"  {MatchText(match)}");
        }
        ShowSeats(matches);
        seatId = ReadId("Номер места (0 — назад)");
    }

    if (seatId is int id)
    {
        Checkout(office.StartSubscriptionSale(id, DateTimeOffset.Now));
    }
}

void Checkout(Sale sale)
{
    try
    {
        if (ConsoleMenu.Interactive)
        {
            Console.Clear();
            var details = sale.Reservation is { } reservation
                ? $"Выкуп брони №{reservation.Id} · продажа №{sale.Id}"
                : $"Продажа №{sale.Id} · Прототип";
            ConsoleMenu.ShowTitle("ОПЛАТА", details);
        }
        ShowPlaces(sale.Places);
        WriteLine($"  К оплате   {Money(sale.Total)}", ConsoleColor.Cyan);
        Console.WriteLine($"  Оплатить до {sale.ExpiresAt:HH:mm:ss}");

        if (!ConsoleMenu.Confirm("Подтвердить оплату", sale.ExpiresAt))
        {
            var message = sale.Reservation?.IsActive(DateTimeOffset.Now) == true
                ? "Выкуп отменён. Бронь действует до своего исходного срока."
                : "Покупка отменена. Места освобождены.";
            WriteLine(message, ConsoleColor.Yellow);
            office.CancelSale(sale);
            ConsoleMenu.Pause();
            return;
        }

        var documents = office.CompleteSale(sale, DateTimeOffset.Now);
        Header("ПОКУПКА ОФОРМЛЕНА");
        WriteLine($"✓  Продажа №{sale.Id} завершена. Оплачено {Money(sale.Total)}.", ConsoleColor.Green);
        foreach (var document in documents)
        {
            Console.WriteLine($"  {DocumentText(document)}");
        }
        ConsoleMenu.Pause();
    }
    finally
    {
        office.CancelSale(sale);
    }
}

void Refund(bool subscription)
{
    var title = subscription ? "ВОЗВРАТ АБОНЕМЕНТА" : "ВОЗВРАТ БИЛЕТОВ";
    var now = DateTimeOffset.Now;
    var activeDocuments = office.GetRefundableDocuments(subscription, now);
    if (activeDocuments.Count == 0)
    {
        Header(title);
        WriteLine(subscription
            ? "Нет абонементов, доступных для возврата."
            : "Нет билетов, доступных для возврата.", ConsoleColor.Yellow);
        ConsoleMenu.Pause();
        return;
    }

    if (ConsoleMenu.Interactive)
    {
        Func<IReadOnlyList<AdmissionDocument>, string>? summarize = null;
        if (!subscription)
        {
            summarize = selected => $"Выбрано: {selected.Count}   К возврату: {Money(selected.Sum(document => document.PaidPrice))}";
        }
        var documents = ConsoleMenu.SelectItems(title, activeDocuments, DocumentText,
            subscription ? "Выбери абонемент для возврата целиком" : "Отметь билеты для возврата", summarize);
        if (documents is not null)
        {
            Header("ПОДТВЕРЖДЕНИЕ ВОЗВРАТА");
            foreach (var document in documents)
            {
                Console.WriteLine($"  {DocumentText(document)}");
            }
            ConfirmRefund(documents.Select(document => document.Id).ToArray(), subscription);
        }
        return;
    }

    Header(title);
    foreach (var document in activeDocuments)
    {
        Console.WriteLine($"  {DocumentText(document)}");
    }

    if (subscription)
    {
        var id = ReadId("Номер абонемента (0 — назад)");
        if (id is int number)
        {
            ConfirmRefund([number], subscription);
        }
    }
    else
    {
        var ids = ReadIds("Номера билетов через запятую (0 — назад)");
        if (ids is not null)
        {
            ConfirmRefund(ids, subscription);
        }
    }
}

void ConfirmRefund(int[] ids, bool subscription)
{
    var total = office.GetRefundTotal(ids, subscription, DateTimeOffset.Now);
    WriteLine($"  К возврату   {Money(total)}", ConsoleColor.Cyan);
    if (!ConsoleMenu.Confirm("Подтвердить выплату"))
    {
        WriteLine("Возврат отменён.", ConsoleColor.Yellow);
        ConsoleMenu.Pause();
        return;
    }

    var paid = office.CompleteRefund(ids, subscription, DateTimeOffset.Now);
    WriteLine($"✓  Возврат оформлен. Выплачено {Money(paid)}. Места освобождены.", ConsoleColor.Green);
    ConsoleMenu.Pause();
}

void ManageReservation()
{
    var choice = ConsoleMenu.Select("БРОНИРОВАНИЕ МЕСТ",
        [("1", "Создать бронь"), ("2", "Отменить бронь"), ("3", "Выкупить бронь"), ("0", "Назад")],
        "Бронь действует 24 часа", liveStatus: ActiveReservationsStatus);

    switch (choice)
    {
        case "1":
            var match = ChooseMatch();
            if (match is null)
            {
                return;
            }
            if (ConsoleMenu.Interactive && !office.CanReserveSeats(match, DateTimeOffset.Now))
            {
                throw new TicketOfficeException("Бронь можно создать минимум за 25 часов до начала матча.");
            }
            int[]? seats;
            if (ConsoleMenu.Interactive)
            {
                seats = SeatPicker.Select(office, [match], multiple: true);
            }
            else
            {
                ShowSeats([match]);
                seats = ReadIds("Номера мест через запятую (0 — назад)");
            }
            if (seats is null)
            {
                return;
            }
            if (ConsoleMenu.Interactive)
            {
                Header("ПОДТВЕРЖДЕНИЕ БРОНИ", MatchText(match));
                Console.WriteLine($"  Места: {string.Join(", ", seats)}");
                WriteLine($"  Стоимость выкупа   {Money(office.Seats.Where(seat => seats.Contains(seat.Number)).Sum(seat => seat.Price))}",
                    ConsoleColor.Cyan);
            }
            if (!ConsoleMenu.Confirm("Создать бронь"))
            {
                return;
            }
            var reservation = office.ReserveSeats(match.Id, seats, DateTimeOffset.Now);
            WriteLine($"✓  Бронь №{reservation.Id} действует до {reservation.ExpiresAt:dd.MM HH:mm}.", ConsoleColor.Green);
            Console.WriteLine($"  Места: {string.Join(", ", seats)}");
            Console.WriteLine($"  Стоимость выкупа: {Money(reservation.Places.Sum(place => place.Seat.Price))}");
            ConsoleMenu.Pause(() => ReservationStatusText(reservation, DateTimeOffset.Now));
            break;
        case "2":
            var id = ChooseReservation("ОТМЕНА БРОНИ");
            if (id is int number && ConsoleMenu.Confirm($"Отменить бронь №{number}"))
            {
                office.CancelReservation(number, DateTimeOffset.Now);
                WriteLine($"✓  Бронь №{number} отменена. Места освобождены.", ConsoleColor.Green);
                ConsoleMenu.Pause();
            }
            break;
        case "3":
            RedeemReservation();
            break;
        case null or "0":
            break;
        default:
            throw new TicketOfficeException("Выбери пункт от 0 до 3.");
    }
}

int? ChooseReservation(string title)
{
    if (!ConsoleMenu.Interactive)
    {
        return ReadId("Номер брони (0 — назад)");
    }

    var reservations = office.GetActiveReservations(DateTimeOffset.Now);
    if (reservations.Count == 0)
    {
        Header(title);
        WriteLine("Активных броней нет.", ConsoleColor.Yellow);
        ConsoleMenu.Pause();
        return null;
    }

    var selected = ConsoleMenu.SelectItems(title, reservations, reservation =>
        $"Бронь №{reservation.Id} · до {reservation.ExpiresAt:dd.MM HH:mm} · {reservation.Places[0].Match.Name} · мест: {reservation.Places.Length}",
        "Выбери действующую бронь", liveStatus: reservation => ReservationStatusText(reservation, DateTimeOffset.Now));
    if (selected is null)
    {
        return null;
    }

    var reservation = selected[0];
    Header(title, $"Бронь №{reservation.Id} · до {reservation.ExpiresAt:dd.MM HH:mm}");
    ShowPlaces(reservation.Places);
    return reservation.Id;
}

string ActiveReservationsStatus()
{
    var now = DateTimeOffset.Now;
    var reservations = office.GetActiveReservations(now);
    if (reservations.Count == 0)
    {
        return "Активных броней нет";
    }

    var nextExpirations = reservations.OrderBy(reservation => reservation.ExpiresAt).Take(2);
    var timers = string.Join("   ", nextExpirations.Select(reservation =>
        $"№{reservation.Id} {FormatRemaining(reservation.ExpiresAt - now)}"));
    var other = reservations.Count > 2 ? $"   + ещё {reservations.Count - 2}" : "";
    return $"Активные брони: {reservations.Count}  ·  Осталось  {timers}{other}";
}

static string ReservationStatusText(Reservation reservation, DateTimeOffset now) =>
    reservation.IsActive(now)
        ? $"Бронь №{reservation.Id} · ОСТАЛОСЬ {FormatRemaining(reservation.ExpiresAt - now)}"
        : $"Бронь №{reservation.Id} истекла";

static string FormatRemaining(TimeSpan remaining)
{
    var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
    return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
}

static void ShowPlaces(IEnumerable<MatchSeat> places)
{
    foreach (var group in places.GroupBy(place => place.Match))
    {
        Console.WriteLine($"  {MatchText(group.Key)}");
        Console.WriteLine($"    Места: {string.Join(", ", group.Select(place => place.Seat.Number))}");
    }
}

Match? ChooseMatch()
{
    var options = office.Matches.Select(match => (match.Id.ToString(), MatchText(match))).ToList();
    options.Add(("0", "Назад"));
    var choice = ConsoleMenu.Select("ВЫБОР МАТЧА", options, prompt: "Номер матча (0 — назад)");
    if (choice is null or "0")
    {
        return null;
    }
    if (!int.TryParse(choice, out var id) || id <= 0)
    {
        throw new TicketOfficeException("Введи номер матча из списка.");
    }
    return office.FindMatch(id);
}

void ShowSeats(IReadOnlyList<Match> matches)
{
    var now = DateTimeOffset.Now;
    Console.WriteLine();
    WriteLine($"  {"№",2}  {"Сектор",-6}  {"Ряд",3}  {"Цена",8}  Статус", ConsoleColor.Cyan);
    Console.WriteLine("  ────────────────────────────────────────");
    foreach (var seat in office.Seats)
    {
        var availability = matches.Max(match => office.GetAvailability(new MatchSeat(match, seat), now));
        var label = availability switch
        {
            SeatAvailability.Free => "свободно",
            SeatAvailability.Held => "удерживается",
            SeatAvailability.Reserved => "в брони",
            SeatAvailability.Sold => "продано",
            _ => throw new InvalidOperationException()
        };
        var price = seat.Price * matches.Count;
        Console.Write($"  {seat.Number,2}  {seat.Sector,-6}  {seat.Row,3}  {Money(price),8}  ");
        WriteLine(label, availability == SeatAvailability.Free ? ConsoleColor.Green : ConsoleColor.Yellow);
    }
}

static string MatchText(Match match) => $"{match.StartsAt:dd.MM HH:mm}   {match.Name}";

static string DocumentText(AdmissionDocument document)
{
    var places = document.Places;
    var label = document is Subscription ? "Абонемент" : "Билет";
    var matches = string.Join(", ", places.Select(place => MatchText(place.Match)));
    return $"{label} №{document.Id} · место {places[0].Seat.Number} · {Money(document.PaidPrice)} · {matches}";
}

static string Money(decimal amount) => $"{amount:0.##} ₽";

static string? Read(string prompt)
{
    Console.Write($"  {prompt}: ");
    return Console.ReadLine()?.Trim();
}

static int[]? ReadIds(string prompt)
{
    var input = Read(prompt);
    if (input is null or "0")
    {
        return null;
    }
    var ids = new List<int>();
    foreach (var part in input.Split(',', StringSplitOptions.TrimEntries))
    {
        if (!int.TryParse(part, out var id) || id <= 0)
        {
            throw new TicketOfficeException("Введи положительные номера. Несколько номеров раздели запятыми.");
        }
        ids.Add(id);
    }
    return ids.ToArray();
}

static int? ReadId(string prompt)
{
    var ids = ReadIds(prompt);
    if (ids is null)
    {
        return null;
    }
    if (ids.Length != 1)
    {
        throw new TicketOfficeException("Введи один номер.");
    }
    return ids[0];
}

static void Header(string title, string? subtitle = null)
{
    if (ConsoleMenu.Interactive)
    {
        Console.Clear();
    }
    ConsoleMenu.ShowTitle(title, subtitle);
}

static void WriteLine(string message, ConsoleColor color)
{
    if (Console.IsOutputRedirected)
    {
        Console.WriteLine(message);
        return;
    }
    var previousColor = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = previousColor;
}
