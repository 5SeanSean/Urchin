using Microsoft.AspNetCore.Identity;

namespace Urchin.Models;

public class ApplicationUser : IdentityUser
{
    public ICollection<Conversation> Conversations { get; set; } = [];
}