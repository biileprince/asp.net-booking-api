using booking.Domain.Entities;

namespace booking.Contracts;

public sealed record CreateSlotRequest(DateTime StartUtc, DateTime EndUtc);

public sealed record CreateBookingRequest(Guid SlotId);

public sealed record BookingResponse(
    Guid Id,
    Guid SlotId,
    Guid CustomerId,
    BookingStatus Status,
    DateTime CreatedAtUtc,
    DateTime? SlotStartUtc,
    DateTime? SlotEndUtc,
    Guid? ProviderId);
