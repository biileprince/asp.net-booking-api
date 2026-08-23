using booking.Domain.Entities;

namespace booking.Contracts;

public sealed record RegisterRequest(string FullName, string Email, string Password, UserRole Role = UserRole.Customer);

public sealed record LoginRequest(string Email, string Password);

public sealed record AuthResponse(Guid UserId, string FullName, string Email, string Role, string Token);
