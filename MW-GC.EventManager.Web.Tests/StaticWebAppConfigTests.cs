using System.Text.Json;
using System.Text.Json.Nodes;

namespace MW_GC.EventManager.Web.Tests;

// Issue #101: every API Function is AuthorizationLevel.Anonymous, so the SWA route table in
// staticwebapp.config.json is the ONLY access gate. These tests read the real file and pin it:
// an exact allow-list of anonymous routes, everything else admin, and the order that keeps it so
// (SWA uses the first matching route). Each check is a function of the parsed config so the
// mutation tests can show it fails on a loosened copy.
[TestClass]
public sealed class StaticWebAppConfigTests
{
    // Every route a signed-out visitor may reach, and why. Adding one here is a security decision.
    private static readonly string[] AnonymousRoutes =
    [
        "/manifest.webmanifest",              // existing: the PWA manifest
        "/feedback/*",                        // #101: the feedback page (served as index.html by navigationFallback)
        "/api/feedback/*",                    // #101: the two anonymous API routes (GET and POST only)
        "/_framework/*",                      // #101: the Blazor runtime and app assemblies the page needs
        "/_content/*",                        // #101: Fluent UI's static files (reboot.css, scripts)
        "/css/app.css",                       // #101: the app stylesheet index.html links
        "/js/focus.js",                       // #101: the script index.html loads before Blazor starts
        "/MW-GC.EventManager.Web.styles.css", // #101: the scoped component styles index.html links
        "/favicon.png",                       // #101: the icon index.html links
    ];

    // Routes that carry no role check at all: a rewrite, a redirect and a fixed 404.
    private static readonly string[] RoutesWithoutRoles = ["/login", "/logout", "/.auth/login/github"];

    private static JsonObject Config() => (JsonObject)JsonNode.Parse(File.ReadAllText(ConfigPath()))!;

    [TestMethod]
    public void TheConfigOnDisk_PassesEveryCheck() => Assert.IsEmpty(Problems(Config()), string.Join("\n", Problems(Config())));

    [TestMethod]
    public void AnonymousRoutes_AreExactlyTheAllowList()
    {
        var anonymous = Routes(Config()).Where(r => Roles(r).Contains("anonymous")).Select(Route).ToList();
        CollectionAssert.AreEquivalent(AnonymousRoutes, anonymous);
    }

    [TestMethod]
    public void TheApiAndCatchAllRules_RequireAdmin_AndOnlyAdmin()
    {
        var routes = Routes(Config());
        CollectionAssert.AreEqual(new[] { "admin" }, Roles(routes.Single(r => Route(r) == "/api/*")).ToArray());
        CollectionAssert.AreEqual(new[] { "admin" }, Roles(routes.Single(r => Route(r) == "*")).ToArray());
        Assert.AreEqual("*", Route(routes[^1]), "the catch-all is the last rule");
        Assert.AreEqual("/api/*", Route(routes[^2]), "/api/* comes right before the catch-all");
    }

    [TestMethod]
    public void TheAnonymousApiRule_IsLimitedToGetAndPost()
    {
        var rule = Routes(Config()).Single(r => Route(r) == "/api/feedback/*");
        CollectionAssert.AreEquivalent(new[] { "GET", "POST" }, rule["methods"]!.AsArray().Select(m => m!.GetValue<string>()).ToArray());
    }

    // ---- Mutation proofs: the checks fail when the boundary is loosened ----------------------

    [TestMethod]
    public void Mutation_LoosenedApiRule_IsCaught()
    {
        var config = Config();
        Routes(config).Single(r => Route(r) == "/api/*")["allowedRoles"] = new JsonArray("anonymous");
        var problems = Problems(config);
        Assert.IsNotEmpty(problems);
        Assert.IsTrue(problems.Any(p => p.Contains("/api/*")), string.Join("\n", problems));
    }

    [TestMethod]
    public void Mutation_AuthenticatedOnTheCatchAll_IsCaught()
    {
        var config = Config();
        Routes(config).Single(r => Route(r) == "*")["allowedRoles"] = new JsonArray("authenticated");
        Assert.IsTrue(Problems(config).Any(p => p.Contains("'*'")), string.Join("\n", Problems(config)));
    }

    [TestMethod]
    public void Mutation_AnonymousRuleAfterTheApiRule_IsCaught()
    {
        var config = Config();
        var routes = (JsonArray)config["routes"]!;
        var rule = routes.Single(r => Route(r!.AsObject()) == "/api/feedback/*")!;
        routes.Remove(rule);
        routes.Insert(routes.Count - 1, rule); // after /api/*, before *: it would never match
        Assert.IsTrue(Problems(config).Any(p => p.Contains("/api/feedback/*") && p.Contains("after")), string.Join("\n", Problems(config)));
    }

    [TestMethod]
    public void Mutation_AnExtraAnonymousRoute_IsCaught()
    {
        var config = Config();
        ((JsonArray)config["routes"]!).Insert(0, new JsonObject { ["route"] = "/api/events*", ["allowedRoles"] = new JsonArray("anonymous") });
        Assert.IsTrue(Problems(config).Any(p => p.Contains("/api/events*")), string.Join("\n", Problems(config)));
    }

    [TestMethod]
    public void Mutation_ARuleWithoutRoles_IsCaught()
    {
        var config = Config();
        ((JsonArray)config["routes"]!).Insert(0, new JsonObject { ["route"] = "/games" });
        Assert.IsTrue(Problems(config).Any(p => p.Contains("/games")), string.Join("\n", Problems(config)));
    }

    // ---- The checks -----------------------------------------------------------------------------

    // Every way the config could open the app to a signed-out visitor, as readable messages.
    private static List<string> Problems(JsonObject config)
    {
        var problems = new List<string>();
        var routes = Routes(config);
        var api = routes.FindIndex(r => Route(r) == "/api/*");
        var all = routes.FindIndex(r => Route(r) == "*");
        if (api < 0) problems.Add("no '/api/*' rule");
        if (all < 0) problems.Add("no catch-all '*' rule");

        for (var i = 0; i < routes.Count; i++)
        {
            var rule = routes[i];
            var route = Route(rule);
            var roles = Roles(rule);
            if (route is "/api/*" or "*")
            {
                if (!roles.SequenceEqual(["admin"]))
                    problems.Add($"'{route}' must allow only admin, allows [{string.Join(", ", roles)}]");
                continue;
            }
            if (roles.Count == 0)
            {
                if (!RoutesWithoutRoles.Contains(route)) problems.Add($"'{route}' has no allowedRoles: it would be open to everyone");
                continue;
            }
            if (roles.Contains("anonymous") || roles.Contains("authenticated"))
            {
                if (!AnonymousRoutes.Contains(route))
                    problems.Add($"'{route}' allows [{string.Join(", ", roles)}] but is not in the anonymous allow-list");
                else if (roles.Contains("authenticated"))
                    problems.Add($"'{route}' allows authenticated: only anonymous or admin are expected");
                if ((api >= 0 && i > api) || (all >= 0 && i > all))
                    problems.Add($"'{route}' comes after '/api/*' or '*': it can never match");
            }
            else if (!roles.SequenceEqual(["admin"]))
            {
                problems.Add($"'{route}' allows [{string.Join(", ", roles)}]: expected admin");
            }
        }

        foreach (var expected in AnonymousRoutes)
            if (!routes.Any(r => Route(r) == expected && Roles(r).Contains("anonymous")))
                problems.Add($"allow-listed '{expected}' is missing");

        // SWA answers a missing role with 401; the override must stay a sign-in redirect.
        var unauthorized = config["responseOverrides"]?["401"]?["redirect"]?.GetValue<string>();
        if (unauthorized is null || !unauthorized.StartsWith("/.auth/login/aad", StringComparison.Ordinal))
            problems.Add("responseOverrides.401 no longer redirects to the AAD sign-in");
        return problems;
    }

    private static List<JsonObject> Routes(JsonObject config) => config["routes"]!.AsArray().Select(r => r!.AsObject()).ToList();
    private static string Route(JsonObject rule) => rule["route"]!.GetValue<string>();
    private static List<string> Roles(JsonObject rule) =>
        rule["allowedRoles"] is JsonArray roles ? roles.Select(r => r!.GetValue<string>()).ToList() : [];

    // The file in the repository, found by walking up from the test output to the solution.
    private static string ConfigPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "MW-GC.EventManager.Web", "staticwebapp.config.json");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("staticwebapp.config.json not found above " + AppContext.BaseDirectory);
    }
}
