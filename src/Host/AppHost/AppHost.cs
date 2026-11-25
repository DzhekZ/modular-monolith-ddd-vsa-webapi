// Local development orchestrator: the README "Quick start" as one `dotnet run`.
//
//   1. backing services       PostgreSQL, RabbitMQ, Keycloak (realm imported from keycloak/), Redis when AppHost:UseRedis
//   2. database migrations    `dotnet ef database update` per module, in the order of `make ef-update-all`
//   3. the API                src/Host/Host on http://localhost:5001, started only after every migration succeeded
//
// The Aspire dashboard (http://localhost:18888) replaces the mm.aspire-dashboard container: the API sends its
// traces, metrics and logs to the dashboard's OTLP endpoint. Credentials and ports match docker-compose.yml, so
// the README, keycloak/README.md and the Postman collection keep working unchanged.
// Development only: shared environments apply the reviewed SQL scripts under migrations/ instead.

// WithoutHttpsCertificate is still marked experimental by Aspire.
#pragma warning disable ASPIRECERTIFICATES001

var builder = DistributedApplication.CreateBuilder(args);

var repositoryRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".."));

// ── Backing services ─────────────────────────────────────────────────────────────────────────────────────────────

var postgresUser = builder.AddParameter("postgres-user");
var postgresPassword = builder.AddParameter("postgres-password", secret: true);
var postgres = builder.AddPostgres("postgres", postgresUser, postgresPassword, port: 5432)
    .WithDataVolume();
var database = postgres.AddDatabase("database", databaseName: "modular-monolith-db");

var rabbitMqUser = builder.AddParameter("rabbitmq-user");
var rabbitMqPassword = builder.AddParameter("rabbitmq-password", secret: true);
var rabbitMq = builder.AddRabbitMQ("rabbitmq", rabbitMqUser, rabbitMqPassword, port: 5672)
    .WithManagementPlugin(port: 15672)
    .WithDataVolume();

var keycloakAdmin = builder.AddParameter("keycloak-admin");
var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", secret: true);
var keycloak = builder.AddKeycloak("keycloak", port: 8080, keycloakAdmin, keycloakAdminPassword)
    // AppHost:KeycloakImageTag in appsettings.json. 26.7 is the version docker-compose.yml and the tests use.
    // `latest` (26.8.x at the time of writing) answers 401 to the backend-api service account on the Admin REST
    // API, which breaks seeding, user lookups and the store-owner role grant. Keycloak keeps its data in the
    // volume and cannot be downgraded: delete the keycloak data volume after switching back to an older tag.
    .WithImageTag(builder.Configuration["AppHost:KeycloakImageTag"] ?? "26.7")
    // Plain http on 8080 like docker-compose.yml: Aspire would otherwise serve Keycloak over https with the
    // ASP.NET Core developer certificate, and the issuer in every token would change with it.
    .WithoutHttpsCertificate()
    // --import-realm skips a realm that already exists: after editing keycloak/realm-modular-monolith.json
    // delete the keycloak data volume (Docker Desktop, or `docker volume ls`) to import it again.
    .WithDataVolume()
    .WithRealmImport(Path.Combine(repositoryRoot, "keycloak"));

// ── Database migrations ──────────────────────────────────────────────────────────────────────────────────────────
// The API refuses to boot while a module has pending migrations (MigrationGuard), so each module's DbContext is
// brought up to date first, one after another, exactly like `make ef-update-all`. dotnet-ef comes from the local
// tool manifest (.config/dotnet-tools.json), so no global tool is needed. The AppHost build already built
// src/Host/Host (build-only project reference in AppHost.csproj), hence --no-build.

#if DEBUG
const string buildConfiguration = "Debug";
#else
const string buildConfiguration = "Release";
#endif

const string startupProject = "src/Host/Host";

IResourceBuilder<ExecutableResource> previousStep = builder
    .AddExecutable("dotnet-tool-restore", "dotnet", repositoryRoot, "tool", "restore")
    .ExcludeFromManifest();

foreach (var (module, project, dbContext) in new (string Module, string Project, string DbContext)[]
         {
             ("outbox", "src/Modules/Outbox/Outbox", "OutboxDbContext"),
             ("notifications", "src/Modules/Notifications/Notifications.Infrastructure", "NotificationsDbContext"),
             ("products", "src/Modules/Products/Products.Infrastructure", "ProductsDbContext"),
             ("inventory", "src/Modules/Inventory/Inventory.Infrastructure", "InventoryDbContext"),
         })
{
    previousStep = builder
        .AddExecutable($"migrate-{module}", "dotnet", repositoryRoot,
            "ef", "database", "update",
            "--project", project,
            "--startup-project", startupProject,
            "--context", dbContext,
            "--configuration", buildConfiguration,
            "--no-build")
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        .WithEnvironment("DatabaseOptions__ConnectionString", database)
        .WaitFor(database)
        .WaitForCompletion(previousStep)
        .WithParentRelationship(database)
        .ExcludeFromManifest();
}

// ── The API ──────────────────────────────────────────────────────────────────────────────────────────────────────
// The API reports service.name = ObservabilityOptions.AppName and service.instance.id = the machine name, so the
// dashboard lists its traces and metrics under the telemetry source "modular-monolith-<machine>".
// Environment variables win over src/Host/Host/Configurations/*.json, the same way docker-compose.yml overrides them.

var api = builder.AddDotnetProject("host", Path.Combine(repositoryRoot, "src", "Host", "Host", "Host.csproj"),
        options => options.ExcludeLaunchProfile = true)
    .WithHttpEndpoint(port: 5001, name: "http")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("DatabaseOptions__ConnectionString",
        ReferenceExpression.Create($"{database};Include Error Detail=true;Application Name=modular-monolith-api"))
    .WithEnvironment("RabbitMqOptions__Host", rabbitMq.GetEndpoint("tcp").Property(EndpointProperty.Host))
    .WithEnvironment("RabbitMqOptions__Port", rabbitMq.GetEndpoint("tcp").Property(EndpointProperty.Port))
    .WithEnvironment("RabbitMqOptions__Username", rabbitMqUser)
    .WithEnvironment("RabbitMqOptions__Password", rabbitMqPassword)
    .WithEnvironment("KeycloakOptions__BaseUrl", keycloak.GetEndpoint("http"))
    .WithEnvironment("ObservabilityOptions__LogSink", "Otlp")
    .WithEnvironment("ObservabilityOptions__OtlpProtocol", "Grpc")
    .WithEnvironment(context =>
    {
        // Aspire points OTEL_EXPORTER_OTLP_ENDPOINT at its dashboard; the API reads its own option instead.
        if (context.EnvironmentVariables.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var otlpEndpoint))
        {
            context.EnvironmentVariables["ObservabilityOptions__OtlpEndpoint"] = otlpEndpoint;
        }
    })
    .WithHttpHealthCheck("/health/ready")
    .WaitFor(database)
    .WaitFor(rabbitMq)
    .WaitFor(keycloak)
    .WaitForCompletion(previousStep);

api.WithUrl(ReferenceExpression.Create($"{api.GetEndpoint("http")}/swagger"), "Swagger UI");
api.WithUrl(ReferenceExpression.Create($"{api.GetEndpoint("http")}/hangfire"), "Hangfire");

// ── Optional Redis (FusionCache L2 + backplane, SignalR backplane) ───────────────────────────────────────────────
// Off by default, like docker-compose.yml (CachingOptions__UseRedis=false). Turn on with AppHost:UseRedis=true
// in appsettings.json, or `dotnet run -- --AppHost:UseRedis=true`.

if (bool.TryParse(builder.Configuration["AppHost:UseRedis"], out var useRedis) && useRedis)
{
    var redisPassword = builder.AddParameter("redis-password", secret: true);
    var redis = builder.AddRedis("redis", port: 6379, password: redisPassword)
        .WithoutHttpsCertificate();
    var redisHost = redis.GetEndpoint("tcp").Property(EndpointProperty.Host);
    var redisPort = redis.GetEndpoint("tcp").Property(EndpointProperty.Port);

    api
        .WithEnvironment("CachingOptions__UseRedis", "true")
        .WithEnvironment("CachingOptions__Redis__Host", redisHost)
        .WithEnvironment("CachingOptions__Redis__Port", redisPort)
        .WithEnvironment("CachingOptions__Redis__Password", redisPassword)
        .WithEnvironment("SignalROptions__UseRedisBackplane", "true")
        .WithEnvironment("SignalROptions__RedisConnectionString",
            ReferenceExpression.Create($"{redisHost}:{redisPort},password={redisPassword},abortConnect=false"))
        .WaitFor(redis);
}

await builder.Build().RunAsync();
