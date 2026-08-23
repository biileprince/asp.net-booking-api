using booking.Domain.Entities;

namespace booking.Auth;

public interface ITokenService
{
    string CreateToken(User user);
}
