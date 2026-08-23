namespace booking.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "booking-api";
    public string Audience { get; set; } = "booking-client";
    public string Key { get; set; } = "replace-this-with-a-very-long-dev-key";
    public int ExpiresMinutes { get; set; } = 60;
}
