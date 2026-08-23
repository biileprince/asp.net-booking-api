using System.Security.Claims;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using booking.Auth;
using booking.Contracts;
using booking.Data;
using booking.Domain.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services.AddDbContext<BookingDbContext>(options =>
	options.UseInMemoryDatabase("booking-db"));

builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services
	.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
	.AddJwtBearer(options =>
	{
		options.TokenValidationParameters = new TokenValidationParameters
		{
			ValidateIssuer = true,
			ValidateAudience = true,
			ValidateIssuerSigningKey = true,
			ValidateLifetime = true,
			ValidIssuer = jwtOptions.Issuer,
			ValidAudience = jwtOptions.Audience,
			IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key))
		};
	});

builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
	errorApp.Run(async context =>
	{
		context.Response.StatusCode = StatusCodes.Status500InternalServerError;
		await context.Response.WriteAsJsonAsync(new
		{
			title = "An unexpected error occurred.",
			status = StatusCodes.Status500InternalServerError
		});
	});
});

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new
{
	message = "Booking API is running.",
	docs = "/swagger"
}));

app.MapPost("/api/auth/register", async (
	RegisterRequest request,
	BookingDbContext dbContext,
	IPasswordHasher<User> passwordHasher,
	ITokenService tokenService) =>
{
	var normalizedEmail = request.Email.Trim().ToLowerInvariant();
	var existingUser = await dbContext.Users.FirstOrDefaultAsync(user => user.Email == normalizedEmail);
	if (existingUser is not null)
	{
		return Results.Conflict(new { message = "Email is already in use." });
	}

	var user = new User
	{
		FullName = request.FullName.Trim(),
		Email = normalizedEmail,
		Role = request.Role
	};

	user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
	dbContext.Users.Add(user);
	await dbContext.SaveChangesAsync();

	var token = tokenService.CreateToken(user);
	var response = new AuthResponse(user.Id, user.FullName, user.Email, user.Role.ToString(), token);
	return Results.Created($"/api/users/{user.Id}", response);
});

app.MapPost("/api/auth/login", async (
	LoginRequest request,
	BookingDbContext dbContext,
	IPasswordHasher<User> passwordHasher,
	ITokenService tokenService) =>
{
	var normalizedEmail = request.Email.Trim().ToLowerInvariant();
	var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail);
	if (user is null)
	{
		return Results.Unauthorized();
	}

	var verifyResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
	if (verifyResult == PasswordVerificationResult.Failed)
	{
		return Results.Unauthorized();
	}

	var token = tokenService.CreateToken(user);
	var response = new AuthResponse(user.Id, user.FullName, user.Email, user.Role.ToString(), token);
	return Results.Ok(response);
});

var providerGroup = app.MapGroup("/api/providers").RequireAuthorization();

providerGroup.MapPost("/slots", async (
	ClaimsPrincipal principal,
	CreateSlotRequest request,
	BookingDbContext dbContext) =>
{
	var role = principal.FindFirstValue(ClaimTypes.Role);
	if (role is not nameof(UserRole.Provider) and not nameof(UserRole.Admin))
	{
		return Results.Forbid();
	}

	if (request.EndUtc <= request.StartUtc)
	{
		return Results.BadRequest(new { message = "EndUtc must be greater than StartUtc." });
	}

	var providerId = GetUserId(principal);
	if (providerId is null)
	{
		return Results.Unauthorized();
	}

	var overlappingSlotExists = await dbContext.AvailabilitySlots.AnyAsync(slot =>
		slot.ProviderId == providerId.Value &&
		request.StartUtc < slot.EndUtc &&
		slot.StartUtc < request.EndUtc);

	if (overlappingSlotExists)
	{
		return Results.Conflict(new { message = "This slot overlaps with an existing slot." });
	}

	var slot = new AvailabilitySlot
	{
		ProviderId = providerId.Value,
		StartUtc = request.StartUtc,
		EndUtc = request.EndUtc,
		IsBooked = false
	};

	dbContext.AvailabilitySlots.Add(slot);
	await dbContext.SaveChangesAsync();

	return Results.Created($"/api/providers/slots/{slot.Id}", slot);
});

providerGroup.MapGet("/slots/me", async (ClaimsPrincipal principal, BookingDbContext dbContext) =>
{
	var providerId = GetUserId(principal);
	if (providerId is null)
	{
		return Results.Unauthorized();
	}

	var slots = await dbContext.AvailabilitySlots
		.Where(slot => slot.ProviderId == providerId.Value)
		.OrderBy(slot => slot.StartUtc)
		.ToListAsync();

	return Results.Ok(slots);
});

app.MapGet("/api/slots/available", async (
	DateTime? fromUtc,
	DateTime? toUtc,
	BookingDbContext dbContext) =>
{
	var query = dbContext.AvailabilitySlots
		.Where(slot => !slot.IsBooked)
		.AsQueryable();

	if (fromUtc.HasValue)
	{
		query = query.Where(slot => slot.StartUtc >= fromUtc.Value);
	}

	if (toUtc.HasValue)
	{
		query = query.Where(slot => slot.EndUtc <= toUtc.Value);
	}

	var slots = await query
		.OrderBy(slot => slot.StartUtc)
		.ToListAsync();

	return Results.Ok(slots);
});

var bookingGroup = app.MapGroup("/api/bookings").RequireAuthorization();

bookingGroup.MapPost("", async (
	ClaimsPrincipal principal,
	CreateBookingRequest request,
	BookingDbContext dbContext) =>
{
	var customerId = GetUserId(principal);
	if (customerId is null)
	{
		return Results.Unauthorized();
	}

	var slot = await dbContext.AvailabilitySlots.FirstOrDefaultAsync(x => x.Id == request.SlotId);
	if (slot is null)
	{
		return Results.NotFound(new { message = "Slot not found." });
	}

	if (slot.IsBooked)
	{
		return Results.Conflict(new { message = "Slot is already booked." });
	}

	slot.IsBooked = true;

	var booking = new Booking
	{
		SlotId = slot.Id,
		CustomerId = customerId.Value,
		Status = BookingStatus.Pending
	};

	dbContext.Bookings.Add(booking);
	await dbContext.SaveChangesAsync();

	var response = new BookingResponse(
		booking.Id,
		booking.SlotId,
		booking.CustomerId,
		booking.Status,
		booking.CreatedAtUtc,
		slot.StartUtc,
		slot.EndUtc,
		slot.ProviderId);

	return Results.Created($"/api/bookings/{booking.Id}", response);
});

bookingGroup.MapGet("/me", async (ClaimsPrincipal principal, BookingDbContext dbContext) =>
{
	var customerId = GetUserId(principal);
	if (customerId is null)
	{
		return Results.Unauthorized();
	}

	var bookings = await dbContext.Bookings
		.Include(booking => booking.Slot)
		.Where(booking => booking.CustomerId == customerId.Value)
		.OrderByDescending(booking => booking.CreatedAtUtc)
		.Select(booking => new BookingResponse(
			booking.Id,
			booking.SlotId,
			booking.CustomerId,
			booking.Status,
			booking.CreatedAtUtc,
			booking.Slot!.StartUtc,
			booking.Slot.EndUtc,
			booking.Slot.ProviderId))
		.ToListAsync();

	return Results.Ok(bookings);
});

bookingGroup.MapPatch("/{bookingId:guid}/cancel", async (
	Guid bookingId,
	ClaimsPrincipal principal,
	BookingDbContext dbContext) =>
{
	var customerId = GetUserId(principal);
	if (customerId is null)
	{
		return Results.Unauthorized();
	}

	var booking = await dbContext.Bookings
		.Include(x => x.Slot)
		.FirstOrDefaultAsync(x => x.Id == bookingId);

	if (booking is null)
	{
		return Results.NotFound(new { message = "Booking not found." });
	}

	if (booking.CustomerId != customerId.Value)
	{
		return Results.Forbid();
	}

	if (booking.Status is BookingStatus.Cancelled or BookingStatus.Completed)
	{
		return Results.BadRequest(new { message = "Booking cannot be cancelled in its current state." });
	}

	booking.Status = BookingStatus.Cancelled;
	if (booking.Slot is not null)
	{
		booking.Slot.IsBooked = false;
	}

	await dbContext.SaveChangesAsync();
	return Results.Ok(new { message = "Booking cancelled." });
});

providerGroup.MapPatch("/bookings/{bookingId:guid}/confirm", async (
	Guid bookingId,
	ClaimsPrincipal principal,
	BookingDbContext dbContext) =>
{
	var providerId = GetUserId(principal);
	if (providerId is null)
	{
		return Results.Unauthorized();
	}

	var booking = await dbContext.Bookings
		.Include(x => x.Slot)
		.FirstOrDefaultAsync(x => x.Id == bookingId);

	if (booking?.Slot is null)
	{
		return Results.NotFound(new { message = "Booking not found." });
	}

	if (booking.Slot.ProviderId != providerId.Value)
	{
		return Results.Forbid();
	}

	if (booking.Status != BookingStatus.Pending)
	{
		return Results.BadRequest(new { message = "Only pending bookings can be confirmed." });
	}

	booking.Status = BookingStatus.Confirmed;
	await dbContext.SaveChangesAsync();

	return Results.Ok(new { message = "Booking confirmed." });
});

app.Run();

static Guid? GetUserId(ClaimsPrincipal principal)
{
	var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
			  ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

	return Guid.TryParse(sub, out var userId) ? userId : null;
}
