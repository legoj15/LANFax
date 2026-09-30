using System.Security.Principal;

namespace LANFax.App.Logic;

/// <summary>A file's NTFS owner: its SID and, when it resolves, its account name (DOMAIN\user).</summary>
public sealed record FileOwner(string Sid, string Name);

/// <summary>
/// Shared-PC hazard: every Windows user prints into the one machine-wide spool, and the dialog opens on the desktop
/// of whoever runs LANFax. The spooler writes the file as the printing user, so its owner tells whether someone else
/// printed it; the send dialog then says "Printed by &lt;owner&gt;". Owners that are this user, a group this user is in
/// (an elevated administrator's files belong to Administrators) or a service account (the spooler itself) are not shown.
/// </summary>
/// <param name="ownerOf">Reads a file's owner (null when unknown); tests fake it.</param>
public sealed class PrintedBy(Func<string, FileOwner?> ownerOf, string currentUserSid, IReadOnlyCollection<string> currentGroupSids)
{
    private static readonly HashSet<string> ServiceSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", // SYSTEM
        "S-1-5-19", // LOCAL SERVICE
        "S-1-5-20", // NETWORK SERVICE
    };

    /// <summary>For the Windows user running this process.</summary>
    public static PrintedBy ForCurrentUser()
    {
        using var me = WindowsIdentity.GetCurrent();
        var groups = me.Groups?.Select(g => g.Value).ToList() ?? [];
        return new PrintedBy(ReadOwner, me.User?.Value ?? "", groups);
    }

    /// <summary>The account name of <paramref name="path"/>'s owner when that is another user; else null (also on any error).</summary>
    public string? OtherOwner(string path)
    {
        FileOwner? owner;
        try { owner = ownerOf(path); }
        catch (SystemException) { return null; } // I/O, access, identity: unknown owner, nothing shown
        if (owner == null || string.IsNullOrEmpty(owner.Sid)) return null;
        if (string.Equals(owner.Sid, currentUserSid, StringComparison.OrdinalIgnoreCase)) return null;
        if (ServiceSids.Contains(owner.Sid) || currentGroupSids.Contains(owner.Sid, StringComparer.OrdinalIgnoreCase)) return null;
        return string.IsNullOrEmpty(owner.Name) ? owner.Sid : owner.Name;
    }

    /// <summary>The send dialog's document line.</summary>
    public static string DocumentLine(string description, string? otherOwner) =>
        otherOwner == null ? "PDF, " + description : $"PDF, {description}. Printed by {otherOwner}";

    /// <summary>The NTFS owner of <paramref name="path"/>, or null when it cannot be read.</summary>
    public static FileOwner? ReadOwner(string path)
    {
        try
        {
            var sid = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (sid == null) return null;
            string name;
            try { name = sid.Translate(typeof(NTAccount)).Value; }
            catch (IdentityNotMappedException) { name = sid.Value; }
            return new FileOwner(sid.Value, name);
        }
        catch (SystemException) { return null; } // I/O, access, identity: unknown owner, nothing shown
    }
}
