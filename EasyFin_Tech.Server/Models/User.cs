using System;
using System.Collections.Generic;

namespace EasyFin_Tech.Server.Models;

public partial class User
{
    public Guid Id { get; set; }

    public string Email { get; set; } = null!;

    public string Password { get; set; } = null!;

    public virtual ICollection<Client> Clients { get; set; } = new List<Client>();
}
