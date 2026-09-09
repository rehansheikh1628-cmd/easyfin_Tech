using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Data;
using EasyFin_Tech.Server.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace EasyFin_Tech.Server.Services;

public interface ICurrentUserService
{
    Guid? UserId { get; }
    string? UserEmail { get; }
    bool IsAuthenticated { get; }
    Task<Client> EnsureDefaultWorkspaceAsync(Guid userId, string? workspaceName = null, CancellationToken cancellationToken = default);
    Task<List<Guid>> GetAuthorizedClientIdsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> HasClientAccessAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default);
}

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly EasyFinDbContext _dbContext;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor, EasyFinDbContext dbContext)
    {
        _httpContextAccessor = httpContextAccessor;
        _dbContext = dbContext;
    }

    public Guid? UserId
    {
        get
        {
            var claim = _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier);
            if (claim != null && Guid.TryParse(claim.Value, out var guid))
            {
                return guid;
            }
            return null;
        }
    }

    public string? UserEmail => _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.Email)?.Value;

    public bool IsAuthenticated => _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true && UserId.HasValue;

    public async Task<Client> EnsureDefaultWorkspaceAsync(Guid userId, string? workspaceName = null, CancellationToken cancellationToken = default)
    {
        var client = await _dbContext.Clients
            .FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);

        if (client != null)
        {
            // Verify financial year exists for this client
            var hasYear = await _dbContext.FinancialYears
                .AnyAsync(y => y.ClientId == client.Id, cancellationToken);

            if (!hasYear)
            {
                var year = new FinancialYear
                {
                    Id = Guid.NewGuid(),
                    ClientId = client.Id,
                    DisplayName = "2026-27",
                    StartDate = new DateTime(2026, 4, 1),
                    EndDate = new DateTime(2027, 3, 31),
                    Status = 1,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.FinancialYears.Add(year);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return client;
        }

        // Create new default client for this user
        var user = await _dbContext.Users.FindAsync([userId], cancellationToken);
        var emailPrefix = user?.Email.Split('@').FirstOrDefault() ?? "User";
        var name = workspaceName ?? $"{emailPrefix}'s Workspace";

        var newClient = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            ContactPerson = emailPrefix,
            Email = user?.Email ?? "user@easyfin.local",
            Phone = "0000000000",
            BusinessName = name,
            BusinessType = "Corporate",
            Address = "Default Address",
            TaxId = "TAX0000",
            CreatedAt = DateTime.UtcNow
        };

        var initialYear = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = newClient.Id,
            DisplayName = "2026-27",
            StartDate = new DateTime(2026, 4, 1),
            EndDate = new DateTime(2027, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Clients.Add(newClient);
        _dbContext.FinancialYears.Add(initialYear);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return newClient;
    }

    public async Task<List<Guid>> GetAuthorizedClientIdsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Clients
            .Where(c => c.UserId == userId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasClientAccessAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Clients
            .AnyAsync(c => c.Id == clientId && c.UserId == userId, cancellationToken);
    }
}
