using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Shared test harness (REQUIREMENTS §148) that gives a Razor Page model a REAL
/// model-binding pipeline so its POST handler binds posted form fields exactly as the
/// live server does. The refactored §148 form pages (and the wizard host) bind their
/// editable fields via <c>TryUpdateModelAsync(model, name: "")</c> instead of
/// <c>[BindProperty]</c>, so the standalone-page + in-wizard tests must post real form
/// values and let the framework bind them — this harness wires the minimal MVC services
/// (<see cref="MvcServiceCollectionExtensions.AddMvcCore"/>) into the request so binding
/// (incl. DateOnly / enum / nested) works without spinning up a full web host.
/// FAKE names only in every caller.
/// </summary>
internal static class WizardBindingHarness
{
    // One shared provider: AddMvcCore registers IModelMetadataProvider, IModelBinderFactory,
    // IObjectModelValidator and the default value-provider factories (Form / Route / Query),
    // which is everything PageModel.TryUpdateModelAsync needs.
    private static readonly IServiceProvider Services = BuildServices();

    private static IServiceProvider BuildServices()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddLocalization();
        sc.AddMvcCore();
        return sc.BuildServiceProvider();
    }

    /// <summary>A GET-style request context (no posted body) carrying the signed-in user.</summary>
    public static DefaultHttpContext GetContext(ClaimsPrincipal user) =>
        new() { User = user, RequestServices = Services };

    /// <summary>A POST request context whose form carries <paramref name="form"/> (flat, no prefix).</summary>
    public static DefaultHttpContext PostContext(ClaimsPrincipal user, IDictionary<string, string?> form)
    {
        var ctx = new DefaultHttpContext { User = user, RequestServices = Services };
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/x-www-form-urlencoded";
        ctx.Request.Form = new FormCollection(
            form.ToDictionary(kv => kv.Key, kv => new StringValues(kv.Value)));
        return ctx;
    }

    /// <summary>Attach a binding-capable <see cref="PageContext"/> (built over <paramref name="http"/>)
    /// to <paramref name="page"/> so its handler can run model binding.
    /// <para>CRITICAL: <see cref="PageModel.TryUpdateModelAsync{TModel}(TModel,string)"/> builds its
    /// value provider from <see cref="PageContext.ValueProviderFactories"/> (NOT from MvcOptions), so a
    /// hand-built PageContext must be seeded with the default factories or binding silently no-ops.</para></summary>
    public static T Bind<T>(this T page, HttpContext http) where T : PageModel
    {
        var pageContext = new PageContext(
            new ActionContext(http, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary()));
        var mvc = http.RequestServices.GetRequiredService<IOptions<MvcOptions>>().Value;
        foreach (var f in mvc.ValueProviderFactories) pageContext.ValueProviderFactories.Add(f);
        page.PageContext = pageContext;
        return page;
    }
}
