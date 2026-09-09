using System;
using System.Collections.Generic;

namespace EasyFin_Tech.Server.DTOs;

public record LoginRequest(string Email, string Password);

public record RegisterRequest(string FullName, string Email, string Password);

public record UserProfileDto(Guid Id, string Email, string? FullName);

public record ClientWorkspaceDto(Guid Id, string Name, string? BusinessName);

public record AuthResponseDto(
    bool Success,
    string Message,
    UserProfileDto? User,
    ClientWorkspaceDto? ActiveWorkspace,
    List<ClientWorkspaceDto>? Workspaces);
