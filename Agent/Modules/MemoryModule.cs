using System.ClientModel;
using System.Net.Http.Headers;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Memory;
using Domain.Tools.Memory;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Memory;
using Infrastructure.Validation;
using Microsoft.Extensions.AI;
using OpenAI;
using StackExchange.Redis;

namespace Agent.Modules;

public static class MemoryModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddMemory(IConfiguration config)
        {
            var memoryConfig = config.GetSection("Memory");

            services.AddSingleton<MemoryExtractionQueue>();

            // Recall embeds against the Lemonade server already in the stack, one hop away on
            // the same Docker network, which has no key to send.
            var embeddingOptions = new EmbeddingOptions
            {
                BaseAddress = memoryConfig["Embedding:BaseAddress"] ?? "http://lemonade:13305/api/v1/",
                Model = memoryConfig["Embedding:Model"] ?? "Qwen3-Embedding-0.6B-GGUF",
                ApiKey = memoryConfig["Embedding:ApiKey"],
                Dimension = memoryConfig.GetValue("Embedding:Dimension", EmbeddingOptions.DefaultDimension),
                Timeout = TimeSpan.FromSeconds(memoryConfig.GetValue("Embedding:TimeoutSeconds", 5))
            };
            services.AddSingleton(embeddingOptions);

            services.AddSingleton<IMemoryStore, RedisStackMemoryStore>();
            services.AddHostedService(sp => new MemoryIndexVerification(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                RedisStackMemoryStore.IndexName,
                embeddingOptions.Dimension,
                sp.GetRequiredService<ILogger<MemoryIndexVerification>>()));

            services.AddHttpClient<IEmbeddingService, EmbeddingService>(
                    (httpClient, sp) => new EmbeddingService(httpClient, sp.GetRequiredService<EmbeddingOptions>()))
                .ConfigurePrimaryHttpMessageHandler(HostedConnectionPool.CreateHandler)
                // The factory's own two-minute handler rotation would throw the pool away well
                // inside the connection lifetime the handler is configured for, so the handler
                // outlives the factory's default and manages pooling itself.
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

            services.AddSingleton<IMemoryExtractor>(sp =>
            {
                var openRouterConfig = config.GetSection("openRouter");
                var providerRouting = openRouterConfig.GetSection("providerRouting").Get<ProviderRouting>();
                var extractionModel = memoryConfig["Extraction:Model"] ?? "z-ai/glm-4.7-flash";
                var metricsPublisher = sp.GetRequiredService<IMetricsPublisher>();
                var chatClient = new OpenRouterChatClient(
                    openRouterConfig["apiUrl"]!, openRouterConfig["apiKey"]!,
                    extractionModel,
                    maxContextTokens: openRouterConfig.GetValue<int?>("maxContextTokens"),
                    metricsPublisher: metricsPublisher,
                    providerRouting: providerRouting);
                return new OpenRouterMemoryExtractor(
                    chatClient,
                    sp.GetRequiredService<IMemoryStore>(),
                    sp.GetRequiredService<ILogger<OpenRouterMemoryExtractor>>());
            });

            services.AddSingleton<IMemoryConsolidator>(sp =>
            {
                var openRouterConfig = config.GetSection("openRouter");
                var providerRouting = openRouterConfig.GetSection("providerRouting").Get<ProviderRouting>();
                var dreamingModel = memoryConfig["Dreaming:Model"] ?? "z-ai/glm-4.7-flash";
                var metricsPublisher = sp.GetRequiredService<IMetricsPublisher>();
                var chatClient = new OpenRouterChatClient(
                    openRouterConfig["apiUrl"]!, openRouterConfig["apiKey"]!,
                    dreamingModel,
                    maxContextTokens: openRouterConfig.GetValue<int?>("maxContextTokens"),
                    metricsPublisher: metricsPublisher,
                    providerRouting: providerRouting);
                return new OpenRouterMemoryConsolidator(
                    chatClient,
                    sp.GetRequiredService<ILogger<OpenRouterMemoryConsolidator>>());
            });

            var recallOptions = new MemoryRecallOptions
            {
                DefaultLimit = memoryConfig.GetValue("Recall:DefaultLimit", 10),
                IncludePersonalityProfile = memoryConfig.GetValue("Recall:IncludePersonalityProfile", true),
                WindowUserTurns = memoryConfig.GetValue("Recall:WindowUserTurns", 3)
            };
            services.AddSingleton(recallOptions);

            var extractionOptions = new MemoryExtractionOptions
            {
                SimilarityThreshold = memoryConfig.GetValue("Extraction:SimilarityThreshold", 0.85),
                MaxCandidatesPerMessage = memoryConfig.GetValue("Extraction:MaxCandidatesPerMessage", 5),
                WindowMixedTurns = memoryConfig.GetValue("Extraction:WindowMixedTurns", 6)
            };
            services.AddSingleton(extractionOptions);

            var dreamingOptions = new MemoryDreamingOptions
            {
                CronSchedule = memoryConfig["Dreaming:CronSchedule"] ?? "0 3 * * *",
                DecayDays = memoryConfig.GetValue("Dreaming:DecayDays", 30),
                DecayFactor = memoryConfig.GetValue("Dreaming:DecayFactor", 0.9),
                DecayFloor = memoryConfig.GetValue("Dreaming:DecayFloor", 0.1)
            };
            services.AddSingleton(dreamingOptions);

            // The three Jev judgments' bars, beside the rest of memory's tunables. The judge itself
            // is the one every Jev use shares, registered with TypeSafe's settings.
            services.AddSingleton(memoryConfig.GetSection("Judgments").Get<MemoryJudgmentSettings>() ?? new MemoryJudgmentSettings());
            services.AddSingleton<MemoryJudge>();

            services.AddSingleton<IMemoryRecallHook, MemoryRecallHook>();

            services.AddTransient<IDomainToolFeature, MemoryToolFeature>();

            services.AddSingleton<ICronValidator, CronValidator>();
            services.AddHostedService<MemoryExtractionWorker>();
            services.AddHostedService<MemoryDreamingService>();

            return services;
        }
    }
}