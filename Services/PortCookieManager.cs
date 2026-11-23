using Microsoft.AspNetCore.Authentication.Cookies;

namespace CourtBook.Services;

// Browsers share cookies across ports; give each customer port its own cookie name.
public sealed class PortCookieManager : ICookieManager
{
    private readonly ChunkingCookieManager cookies = new();
    private static string Key(HttpContext context, string key) => $"{key}.{context.Connection.LocalPort}";

    public string? GetRequestCookie(HttpContext context, string key) =>
        cookies.GetRequestCookie(context, Key(context, key));

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) =>
        cookies.AppendResponseCookie(context, Key(context, key), value, options);

    public void DeleteCookie(HttpContext context, string key, CookieOptions options) =>
        cookies.DeleteCookie(context, Key(context, key), options);
}
