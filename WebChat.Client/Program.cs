using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WebChat.Client;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Services;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Pipeline;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddScoped<IHubEventDispatcher, HubEventDispatcher>();
builder.Services.AddScoped<ConnectionEventDispatcher>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddWebChatLiveConnection();

builder.Services.AddScoped<IChatSessionService, ChatSessionService>();
builder.Services.AddScoped<IChatMessagingService, ChatMessagingService>();
builder.Services.AddScoped<ITopicService, TopicService>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IApprovalService, ApprovalService>();
builder.Services.AddScoped<AttachmentEndpointResolver>();
builder.Services.AddScoped<IAttachmentService, AttachmentService>();
builder.Services.AddScoped<IAttachmentUploader, HttpAttachmentUploader>();
builder.Services.AddScoped<IDictationService, DictationService>();
builder.Services.AddScoped<IDictationBridge, JsDictationBridge>();
builder.Services.AddScoped<ApprovalResponder>();

builder.Services.AddScoped<ILocalStorageService, LocalStorageService>();

builder.Services.AddWebChatStores();
builder.Services.AddWebChatEffects();

builder.Services.AddScoped<TopicStreams>();
builder.Services.AddScoped<IStreamingService, StreamingService>();
builder.Services.AddScoped<StreamResumeService>();
builder.Services.AddScoped<IStreamResumeService>(sp => sp.GetRequiredService<StreamResumeService>());
builder.Services.AddScoped<IMessagePipeline, MessagePipeline>();

builder.Services.AddScoped<PushNotificationService>();
builder.Services.AddScoped<IPushSubscriptionService>(sp => sp.GetRequiredService<PushNotificationService>());

var app = builder.Build();

// Activate effects that need to run at startup
_ = app.Services.GetRequiredService<SessionRecoveryEffect>();
_ = app.Services.GetRequiredService<ReconnectionEffect>();
_ = app.Services.GetRequiredService<SendMessageEffect>();
_ = app.Services.GetRequiredService<TopicSelectionEffect>();
_ = app.Services.GetRequiredService<TopicDeleteEffect>();
_ = app.Services.GetRequiredService<InitializationEffect>();
_ = app.Services.GetRequiredService<AgentSelectionEffect>();
_ = app.Services.GetRequiredService<UserIdentityEffect>();
_ = app.Services.GetRequiredService<SpaceEffect>();
_ = app.Services.GetRequiredService<AgentActivityEffect>();
_ = app.Services.GetRequiredService<AgentSettingsEffect>();
_ = app.Services.GetRequiredService<StreamResumeEffect>();
_ = app.Services.GetRequiredService<AttachmentEffect>();
_ = app.Services.GetRequiredService<DictationEffect>();
_ = app.Services.GetRequiredService<TopicRenameEffect>();

await app.RunAsync();