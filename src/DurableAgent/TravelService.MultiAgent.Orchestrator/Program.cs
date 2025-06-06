using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using TravelService.MultiAgent.Orchestrator.Interfaces;
using TravelService.MultiAgent.Orchestrator.Services;
using Microsoft.Azure.Cosmos;
using SendGrid;
using Azure.Identity;
using StackExchange.Redis;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Microsoft.SemanticKernel.ChatCompletion;
using TravelService.MultiAgent.Orchestrator.Models;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry;
using static TravelService.MultiAgent.Orchestrator.Helper.Extenstions;
using TravelService.MultiAgent.Orchestrator.TracingDataHandlers;
using AzureFunctions.Extensions.Middleware.Abstractions;
using AzureFunctions.Extensions.Middleware.Infrastructure;
using Microsoft.AspNetCore.Http;
using TravelService.MultiAgent.Orchestrator.Middlewares;
using Azure.Monitor.OpenTelemetry.Exporter;
using Polly;
using Polly.Extensions.Http;
using Microsoft.SemanticKernel.Embeddings;

#pragma warning disable SKEXP0010
#pragma warning disable SKEXP0001

var host = new HostBuilder()
     .ConfigureFunctionsWebApplication(applicationBuilder =>
     {
        applicationBuilder.UseFunctionContextAccessor();
     })
    .ConfigureServices((context, services) =>
    {
       services.AddApplicationInsightsTelemetryWorkerService();
       services.ConfigureFunctionsApplicationInsights();
       services.AddLogging();
       services.AddFunctionContextAccessor();

       #region Environment Variables
       var cosmosdbAccountEndpoint = Environment.GetEnvironmentVariable("CosmosDBAccountEndpoint");
       var openaiEndpoint = Environment.GetEnvironmentVariable("OpenAIEndpoint");
       var openaiChatCompletionDeploymentName = Environment.GetEnvironmentVariable("OpenAIChatCompletionDeploymentName");
       var openaiTextEmbeddingGenerationDeploymentName = Environment.GetEnvironmentVariable("OpenAITextEmbeddingGenerationDeploymentName");
       var userAssignedIdentityClientId = Environment.GetEnvironmentVariable("UserAssignedIdentity");
       var redisConnectionString = Environment.GetEnvironmentVariable("RedisConnectionString");
       var appInsightsConnectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");

       if (string.IsNullOrEmpty(cosmosdbAccountEndpoint) || string.IsNullOrEmpty(openaiEndpoint) 
       || string.IsNullOrEmpty(openaiChatCompletionDeploymentName) || string.IsNullOrEmpty(openaiTextEmbeddingGenerationDeploymentName) 
       || string.IsNullOrEmpty(redisConnectionString) || string.IsNullOrEmpty(appInsightsConnectionString))
       {
          throw new Exception("Missing required environment variables");
       }
       #endregion

       #region OpenTelemetry       
       var openTelemetryResourceBuilder = ResourceBuilder.CreateDefault().AddService(serviceName: "TravelService", serviceVersion: "1.0.0");
       var openTelemetryTracerProvider = Sdk.CreateTracerProviderBuilder()
               .AddOtlpExporter()
               .AddAzureMonitorTraceExporter(c => c.ConnectionString = appInsightsConnectionString)
               .AddSource("TravelService")
               .SetSampler(new AlwaysOnSampler())
               .SetResourceBuilder(openTelemetryResourceBuilder)
               .Build();

       var metricsProvider = Sdk.CreateMeterProviderBuilder().AddAzureMonitorMetricExporter();

       services.AddSingleton<TracerProvider>(openTelemetryTracerProvider);
       services.AddSingleton<ILoggerProvider, OpenTelemetryLoggerProvider>();
       services.AddScoped<IActivityTriggerTracingHandler, ActivityTriggerTracingHandler>();
       services.AddScoped<IOrchestratorTriggerTracingHandler, OrchestratorTriggerTracingHandler>();
       services.AddScoped<IPluginTracingHandler, PluginTracingHandler>();
       services.Configure<OpenTelemetryLoggerOptions>((openTelemetryLoggerOptions) =>
       {
          openTelemetryLoggerOptions.SetResourceBuilder(openTelemetryResourceBuilder);
          openTelemetryLoggerOptions.IncludeFormattedMessage = true;
          openTelemetryLoggerOptions.AddConsoleExporter();
          openTelemetryLoggerOptions.AddAzureMonitorLogExporter(c => c.ConnectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING"));
       }
       );

       services.AddOpenTelemetry(b =>
       {
          b.IncludeFormattedMessage = true;
          b.IncludeScopes = true;
          b.ParseStateValues = true;
       });
       #endregion

       #region Middleware       
       services.AddTransient<IHttpMiddlewareBuilder, HttpMiddlewareBuilder>((sp) =>
       {
          var funcBuilder = new HttpMiddlewareBuilder(sp.GetRequiredService<IHttpContextAccessor>());
          funcBuilder.Use(new HttpExceptionMiddleware(sp.GetRequiredService<ILogger<HttpExceptionMiddleware>>()));
          funcBuilder.Use(new OpenTelemetryHttpMiddleware(sp.GetRequiredService<ILogger<OpenTelemetryHttpMiddleware>>(), openTelemetryTracerProvider.GetTracer("TravelService"), sp.GetRequiredService<TracingContextCache>()));
          return funcBuilder;
       });
       #endregion

       #region CosmosDB

       var credentialOptions = new DefaultAzureCredentialOptions
       {
          TenantId = Environment.GetEnvironmentVariable("TenantId")
       };
       if (!string.IsNullOrEmpty(userAssignedIdentityClientId))
       {
          credentialOptions.ManagedIdentityClientId = userAssignedIdentityClientId;
       }
       services.AddSingleton(s =>
       {
          CosmosClientOptions options = new CosmosClientOptions()
          {
             ConnectionMode = ConnectionMode.Direct
          };

          return new CosmosClient(cosmosdbAccountEndpoint, new DefaultAzureCredential(credentialOptions), options);
       });
       #endregion

       #region OpenAI services
       services.AddSingleton<IChatCompletionService, AzureOpenAIChatCompletionService>(pprovider =>
       {
          return new AzureOpenAIChatCompletionService(
               deploymentName: openaiChatCompletionDeploymentName!,
               credentials: new DefaultAzureCredential(credentialOptions),
               endpoint: openaiEndpoint
           );
       });
       services.AddSingleton<ITextEmbeddingGenerationService, AzureOpenAITextEmbeddingGenerationService>(provider =>
       {
          return new AzureOpenAITextEmbeddingGenerationService(deploymentName: openaiTextEmbeddingGenerationDeploymentName,
               endpoint: openaiEndpoint,
               credential: new DefaultAzureCredential(credentialOptions));
       });
       #endregion

       #region Redis Cache
       services.AddSingleton<IConnectionMultiplexer>(sp =>
       {
          var redisConfiguration = ConfigurationOptions.Parse(redisConnectionString, true);
          return ConnectionMultiplexer.Connect(redisConfiguration);
       });
       #endregion

       services.AddSingleton<ISendGridClient,SendGridClient>(provider =>
        {
           return new SendGridClient(Environment.GetEnvironmentVariable("SendGridApiKey"));
        });

       services.AddScoped<Kernel>(provider =>
       {
          var builder = Kernel.CreateBuilder();

          builder.AddAzureOpenAIChatCompletion(deploymentName: openaiChatCompletionDeploymentName,
               credentials: new DefaultAzureCredential(credentialOptions),
               endpoint: openaiEndpoint);
          return builder.Build();
       });
       services.AddScoped<TracingContextCache>();
       services.AddTransient<HttpInterceptorTracingHandler>();
       services.AddHttpContextAccessor();

       #region Typed clients for microservices and external services
       services.AddHttpClient<IPostmarkServiceClient, PostmarkServiceClient>(client =>
       {
          client.BaseAddress = new Uri("https://api.postmarkapp.com");
          client.DefaultRequestHeaders.Add("Accept", "application/json");
          client.DefaultRequestHeaders.Add("X-Postmark-Server-Token", Environment.GetEnvironmentVariable("PostmarkServerToken"));
       });

       services.AddHttpClient<IFlightServiceClient, FlightServiceClient>(client =>
       {
          var apimUrl = context.HostingEnvironment.IsDevelopment()
        ? Environment.GetEnvironmentVariable("FlightServiceUrl")
        : Environment.GetEnvironmentVariable("ApimUrl") + "/flightservice";
          client.BaseAddress = new Uri(apimUrl + "/api/");
       });

       services.AddHttpClient<IWeatherServiceClient, WeatherServiceClient>(client =>
       {
          var apimUrl = context.HostingEnvironment.IsDevelopment()
        ? Environment.GetEnvironmentVariable("WeatherServiceUrl")
        : Environment.GetEnvironmentVariable("ApimUrl") + "/weatherservice";
          client.BaseAddress = new Uri(apimUrl + "/api/");
       });


       services.AddHttpClient<IUserServiceClient, UserServiceClient>(client =>
       {
          var apimUrl = context.HostingEnvironment.IsDevelopment()
        ? Environment.GetEnvironmentVariable("UserServiceUrl")
        : Environment.GetEnvironmentVariable("ApimUrl") + "/userservice";
          client.BaseAddress = new Uri(apimUrl + "/api/");
       });


       services.AddHttpClient<IBookingServiceClient, BookingServiceClient>(client =>
       {
          var apimUrl = context.HostingEnvironment.IsDevelopment()
        ? Environment.GetEnvironmentVariable("BookingServiceUrl")
        : Environment.GetEnvironmentVariable("ApimUrl") + "/bookingservice";
          client.BaseAddress = new Uri(apimUrl + "/api/");
       });
       #endregion

       services.AddScoped<IPromptyService, PromptyService>();
       services.AddScoped<IKernelService, KernelService>();
       services.AddScoped<INL2SQLService, NL2SQLService>();
       services.AddScoped<ICosmosClientService, CosmosClientService>();
       services.AddScoped<IPostmarkServiceClient, PostmarkServiceClient>();
       services.AddScoped<IFabricAuthService, FabricAuthService>();
       services.AddScoped<IFabricGraphQLService, FabricGraphQLService>();
    })
    .UseDefaultServiceProvider((context, options) =>
    {
       options.ValidateScopes = context.HostingEnvironment.IsDevelopment();
    })
    .Build();

host.Run();
