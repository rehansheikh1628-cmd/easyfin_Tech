using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AccufexDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        AccufexDbContext dbContext,
        IPasswordHasher passwordHasher,
        ICurrentUserService currentUserService,
        ILogger<AuthController> logger)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = "A valid email address is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = "Password must be at least 6 characters long.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var existingUser = await _dbContext.Users
            .AnyAsync(u => u.Email.ToLower() == normalizedEmail, cancellationToken);

        if (existingUser)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Registration Error",
                Detail = "An account with this email address already exists.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var newUser = new User
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            Password = _passwordHasher.HashPassword(request.Password)
        };

        _dbContext.Users.Add(newUser);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Ensure user has their isolated client workspace
        var workspaceName = string.IsNullOrWhiteSpace(request.FullName)
            ? $"{normalizedEmail.Split('@')[0]}'s Workspace"
            : $"{request.FullName.Trim()}'s Workspace";

        var workspace = await _currentUserService.EnsureDefaultWorkspaceAsync(newUser.Id, workspaceName, cancellationToken);

        // Establish authenticated session via Cookie
        await SignInUserAsync(newUser, request.FullName);

        _logger.LogInformation("User {UserId} successfully registered with workspace {WorkspaceId}.", newUser.Id, workspace.Id);

        var response = new AuthResponseDto(
            Success: true,
            Message: "Registration successful.",
            User: new UserProfileDto(newUser.Id, newUser.Email, request.FullName),
            ActiveWorkspace: new ClientWorkspaceDto(workspace.Id, workspace.Name, workspace.BusinessName),
            Workspaces: [new ClientWorkspaceDto(workspace.Id, workspace.Name, workspace.BusinessName)]
        );

        return Ok(response);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = "Email and password are required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var user = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail, cancellationToken);

        if (user == null)
        {
            // Generic message to prevent username enumeration
            return Unauthorized(new ProblemDetails
            {
                Title = "Authentication Failed",
                Detail = "Invalid email or password.",
                Status = StatusCodes.Status401Unauthorized
            });
        }

        bool isValid = _passwordHasher.VerifyHashedPassword(user.Password, request.Password, out bool rehashNeeded);
        if (!isValid)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Authentication Failed",
                Detail = "Invalid email or password.",
                Status = StatusCodes.Status401Unauthorized
            });
        }

        // Transparently upgrade legacy plaintext password to salted PBKDF2 hash
        if (rehashNeeded)
        {
            user.Password = _passwordHasher.HashPassword(request.Password);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Upgraded password to salted PBKDF2 for user {UserId}.", user.Id);
        }

        // Ensure user has at least one default client workspace
        var workspace = await _currentUserService.EnsureDefaultWorkspaceAsync(user.Id, null, cancellationToken);

        var workspaces = await _dbContext.Clients
            .Where(c => c.UserId == user.Id)
            .Select(c => new ClientWorkspaceDto(c.Id, c.Name, c.BusinessName))
            .ToListAsync(cancellationToken);

        // Establish authenticated session via Cookie
        await SignInUserAsync(user, workspace.ContactPerson);

        _logger.LogInformation("User {UserId} successfully logged in.", user.Id);

        var response = new AuthResponseDto(
            Success: true,
            Message: "Login successful.",
            User: new UserProfileDto(user.Id, user.Email, workspace.ContactPerson),
            ActiveWorkspace: new ClientWorkspaceDto(workspace.Id, workspace.Name, workspace.BusinessName),
            Workspaces: workspaces
        );

        return Ok(response);
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { success = true, message = "Logged out successfully." });
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var userId = _currentUserService.UserId;
        if (!userId.HasValue)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Unauthorized",
                Detail = "No authenticated session found.",
                Status = StatusCodes.Status401Unauthorized
            });
        }

        var user = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);

        if (user == null)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Unauthorized();
        }

        var workspaces = await _dbContext.Clients
            .AsNoTracking()
            .Where(c => c.UserId == user.Id)
            .Select(c => new ClientWorkspaceDto(c.Id, c.Name, c.BusinessName))
            .ToListAsync(cancellationToken);

        var activeWorkspace = workspaces.FirstOrDefault();

        var response = new AuthResponseDto(
            Success: true,
            Message: "Current session retrieved.",
            User: new UserProfileDto(user.Id, user.Email, activeWorkspace?.Name),
            ActiveWorkspace: activeWorkspace,
            Workspaces: workspaces
        );

        return Ok(response);
    }

    private async Task SignInUserAsync(User user, string? displayName)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, displayName ?? user.Email)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var authProperties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            authProperties);
    }
}
