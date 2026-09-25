namespace Skylab.Cms.Domain;

/// <summary>
/// The one shared stand-in ("Silinmiş kullanıcı") that replaces an erased
/// person in every actor column. It is the same for everyone, so it links
/// nothing and cannot be signed in as (ADR-0051).
/// </summary>
public static class DeletedUser
{
    public const string Subject = "00000000-0000-4000-8000-000000000000";
}
