namespace FootballTickets;

internal sealed class TicketOffice(DateTimeOffset now)
{
    private static readonly TimeSpan SaleWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TicketCutoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReservationCutoff = TimeSpan.FromHours(25);
    private static readonly TimeSpan ReservationWindow = TimeSpan.FromHours(24);

    private readonly List<AdmissionDocument> _documents = [];
    private readonly Dictionary<int, Reservation> _reservations = [];
    private Sale? _pendingSale;
    private int _nextDocumentId = 1;
    private int _nextSaleId = 1;
    private int _nextReservationId = 1;

    public IReadOnlyList<Match> Matches { get; } =
    [
        new(1, "Север — Волна", now.Date.AddDays(2).AddHours(19)),
        new(2, "Север — Спутник", now.Date.AddDays(7).AddHours(18)),
        new(3, "Север — Маяк", now.AddHours(2))
    ];

    public IReadOnlyList<Match> SubscriptionMatches => [Matches[0], Matches[1]];

    public IReadOnlyList<Seat> Seats { get; } =
        Enumerable.Range(1, 4)
            .SelectMany(row => Enumerable.Range(1, 8)
                .Select(column => new Seat((row - 1) * 8 + column,
                    column <= 4 ? "А" : "Б", row, column <= 4 ? 800 : 1200)))
            .ToArray();

    public Match FindMatch(int matchId) =>
        Matches.FirstOrDefault(match => match.Id == matchId)
        ?? throw new TicketOfficeException($"Матч №{matchId} не найден.");

    public SeatAvailability GetAvailability(MatchSeat place, DateTimeOffset time)
    {
        if (_documents.Any(document => document.IsValid && document.Places.Contains(place)))
        {
            return SeatAvailability.Sold;
        }
        if (_pendingSale is { } sale && time < sale.ExpiresAt && sale.Places.Contains(place))
        {
            return SeatAvailability.Held;
        }
        return _reservations.Values.Any(reservation => reservation.IsActive(time) && reservation.Places.Contains(place))
            ? SeatAvailability.Reserved
            : SeatAvailability.Free;
    }

    public Sale StartSale(int matchId, int[] seatNumbers, DateTimeOffset time)
    {
        var match = FindMatch(matchId);
        EnsureTicketSelectionOpen(match, time);
        var places = SelectPlaces(match, seatNumbers);
        EnsureFree(places, time);
        return HoldPlaces(places, time);
    }

    public Sale UpdateSaleSelection(Sale sale, int[] seatNumbers, DateTimeOffset time)
    {
        if (!ReferenceEquals(_pendingSale, sale) || time >= sale.ExpiresAt)
        {
            throw new TicketOfficeException("Время оплаты вышло. Оформление отменено.");
        }

        if (sale.SingleMatch is not { } match || sale.Reservation is not null)
        {
            throw new TicketOfficeException("Места в этой покупке заданы заранее.");
        }
        var places = seatNumbers.Length == 0 ? [] : SelectPlaces(match, seatNumbers);
        var addedPlaces = places.Except(sale.Places).ToArray();
        if (addedPlaces.Length > 0)
        {
            EnsureTicketSelectionOpen(match, time);
            EnsureFree(addedPlaces, time);
        }

        var updatedSale = sale with { Places = places, Total = places.Sum(place => place.Seat.Price) };
        _pendingSale = updatedSale;
        return updatedSale;
    }

    public Sale StartSubscriptionSale(int seatNumber, DateTimeOffset time)
    {
        if (SubscriptionMatches.Any(match => time >= match.StartsAt))
        {
            throw new TicketOfficeException("Продажа абонемента завершена. Один из матчей уже начался.");
        }
        var seat = FindSeat(seatNumber);
        var places = SubscriptionMatches.Select(match => new MatchSeat(match, seat)).ToArray();
        EnsureFree(places, time);
        return HoldPlaces(places, time, isSubscription: true);
    }

    public Sale StartReservationSale(int reservationId, DateTimeOffset time)
    {
        var reservation = FindActiveReservation(reservationId, time);
        return HoldPlaces(reservation.Places, time, reservation: reservation);
    }

    public IReadOnlyList<AdmissionDocument> CompleteSale(Sale sale, DateTimeOffset time)
    {
        if (!ReferenceEquals(_pendingSale, sale) || time >= sale.ExpiresAt)
        {
            CancelSale(sale);
            throw new TicketOfficeException("Срок оплаты истёк. Оформление отменено.");
        }
        if (sale.Places.Length == 0)
        {
            throw new TicketOfficeException("Выбери хотя бы одно место.");
        }
        if (sale.Places.Any(place => time >= place.Match.StartsAt))
        {
            CancelSale(sale);
            throw new TicketOfficeException("Матч уже начался. Оформление отменено.");
        }

        AdmissionDocument[] issuedDocuments = sale.IsSubscription
            ? [new Subscription(_nextDocumentId++, sale.Total, sale.Places)]
            : sale.Places.Select(place => (AdmissionDocument)new Ticket(_nextDocumentId++, place.Seat.Price, place)).ToArray();

        _documents.AddRange(issuedDocuments);
        if (sale.Reservation is { } reservation)
        {
            reservation.Status = ReservationStatus.Redeemed;
        }
        _pendingSale = null;
        return issuedDocuments;
    }

    public void CancelSale(Sale sale)
    {
        if (ReferenceEquals(_pendingSale, sale))
        {
            _pendingSale = null;
        }
    }

    public Reservation ReserveSeats(int matchId, int[] seatNumbers, DateTimeOffset time)
    {
        var match = FindMatch(matchId);
        if (!CanReserveSeats(match, time))
        {
            throw new TicketOfficeException("Бронь можно создать минимум за 25 часов до начала матча.");
        }

        var places = SelectPlaces(match, seatNumbers);
        EnsureFree(places, time);
        var reservation = new Reservation(_nextReservationId++, time.Add(ReservationWindow), places);
        _reservations.Add(reservation.Id, reservation);
        return reservation;
    }

    public bool CanReserveSeats(Match match, DateTimeOffset time) => match.StartsAt - time >= ReservationCutoff;

    public IReadOnlyList<Reservation> GetActiveReservations(DateTimeOffset time) =>
        _reservations.Values.Where(reservation => reservation.IsActive(time)).OrderBy(reservation => reservation.Id).ToArray();

    public void CancelReservation(int reservationId, DateTimeOffset time)
    {
        var reservation = FindActiveReservation(reservationId, time);
        if (_pendingSale is { } sale && ReferenceEquals(sale.Reservation, reservation) && time < sale.ExpiresAt)
        {
            throw new TicketOfficeException("Бронь уже выкупают. Сначала отмени оформление покупки.");
        }
        reservation.Status = ReservationStatus.Cancelled;
    }

    public IReadOnlyList<AdmissionDocument> GetRefundableDocuments(bool subscription, DateTimeOffset time) =>
        _documents.Where(document => (document is Subscription) == subscription && document.CanRefund(time)).ToArray();

    public decimal GetRefundTotal(int[] documentIds, bool subscription, DateTimeOffset time) =>
        FindRefundableDocuments(documentIds, subscription, time).Sum(document => document.PaidPrice);

    public decimal CompleteRefund(int[] documentIds, bool subscription, DateTimeOffset time)
    {
        var documents = FindRefundableDocuments(documentIds, subscription, time);
        foreach (var document in documents)
        {
            document.Invalidate();
        }
        return documents.Sum(document => document.PaidPrice);
    }

    private Sale HoldPlaces(MatchSeat[] places, DateTimeOffset time, bool isSubscription = false, Reservation? reservation = null)
    {
        if (_pendingSale is { } previous && time < previous.ExpiresAt)
        {
            throw new TicketOfficeException("Сначала заверши или отмени текущую покупку.");
        }

        var total = places.Sum(place => place.Seat.Price);
        var sale = new Sale(_nextSaleId++, places, total, time.Add(SaleWindow),
            isSubscription ? null : places[0].Match, reservation);
        _pendingSale = sale;
        return sale;
    }

    private MatchSeat[] SelectPlaces(Match match, int[] seatNumbers)
    {
        EnsureDistinctSelection(seatNumbers);
        return seatNumbers.Select(number => new MatchSeat(match, FindSeat(number))).ToArray();
    }

    private Seat FindSeat(int number) =>
        Seats.FirstOrDefault(seat => seat.Number == number)
        ?? throw new TicketOfficeException($"Место №{number} не найдено.");

    private Reservation FindActiveReservation(int reservationId, DateTimeOffset time)
    {
        if (!_reservations.TryGetValue(reservationId, out var reservation))
        {
            throw new TicketOfficeException($"Бронь №{reservationId} не найдена.");
        }
        if (!reservation.IsActive(time))
        {
            throw new TicketOfficeException($"Бронь №{reservationId} уже выкуплена, отменена или истекла.");
        }
        return reservation;
    }

    private AdmissionDocument[] FindRefundableDocuments(int[] documentIds, bool subscription, DateTimeOffset time)
    {
        EnsureDistinctSelection(documentIds);
        return documentIds.Select(id =>
        {
            var document = _documents.Find(item => item.Id == id && (item is Subscription) == subscription)
                ?? throw new TicketOfficeException($"Документ №{id} не найден для выбранного вида возврата.");
            if (!document.CanRefund(time))
            {
                throw new TicketOfficeException($"Возврат №{id} недоступен. Документ уже аннулирован или матч начался.");
            }
            return document;
        }).ToArray();
    }

    private void EnsureFree(MatchSeat[] places, DateTimeOffset time)
    {
        foreach (var place in places)
        {
            if (GetAvailability(place, time) != SeatAvailability.Free)
            {
                throw new TicketOfficeException($"Место №{place.Seat.Number} на матч «{place.Match.Name}» недоступно.");
            }
        }
    }

    private static void EnsureTicketSelectionOpen(Match match, DateTimeOffset time)
    {
        if (match.StartsAt - time <= TicketCutoff)
        {
            throw new TicketOfficeException("Выбор новых мест закрывается за 15 минут до начала матча.");
        }
    }

    private static void EnsureDistinctSelection(int[] numbers)
    {
        if (numbers.Length == 0 || numbers.Distinct().Count() != numbers.Length)
        {
            throw new TicketOfficeException("Укажи хотя бы один номер и убери повторы.");
        }
    }
}

internal sealed class TicketOfficeException(string message) : Exception(message);

internal sealed record Match(int Id, string Name, DateTimeOffset StartsAt);

internal sealed record Seat(int Number, string Sector, int Row, decimal Price);

internal sealed record MatchSeat(Match Match, Seat Seat);

internal sealed record Sale(
    int Id, MatchSeat[] Places, decimal Total, DateTimeOffset ExpiresAt, Match? SingleMatch, Reservation? Reservation)
{
    public bool IsSubscription => SingleMatch is null;
}

internal abstract class AdmissionDocument(int id, decimal paidPrice, IReadOnlyList<MatchSeat> places)
{
    public int Id { get; } = id;
    public decimal PaidPrice { get; } = paidPrice;
    public IReadOnlyList<MatchSeat> Places { get; } = places;
    public bool IsValid { get; private set; } = true;

    public bool CanRefund(DateTimeOffset time) => IsValid && Places.All(place => time < place.Match.StartsAt);

    public void Invalidate() => IsValid = false;
}

internal sealed class Ticket(int id, decimal paidPrice, MatchSeat place) : AdmissionDocument(id, paidPrice, [place]);

internal sealed class Subscription(int id, decimal paidPrice, MatchSeat[] places) : AdmissionDocument(id, paidPrice, places);

internal sealed class Reservation(int id, DateTimeOffset expiresAt, MatchSeat[] places)
{
    public int Id { get; } = id;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public MatchSeat[] Places { get; } = places;
    public ReservationStatus Status { get; set; } = ReservationStatus.Active;

    public bool IsActive(DateTimeOffset time) => Status == ReservationStatus.Active && time < ExpiresAt;
}

internal enum ReservationStatus
{
    Active,
    Cancelled,
    Redeemed
}

internal enum SeatAvailability
{
    Free,
    Held,
    Reserved,
    Sold
}
