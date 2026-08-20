using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Mcp.Hosting;

namespace McpChannelServiceBus.Services;

public sealed class ServiceBusProcessorService(
    ServiceBusProcessor processor,
    ChannelNotificationEmitter notificationEmitter,
    ILogger<ServiceBusProcessorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);
        logger.LogInformation("Service Bus processor started");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            await processor.StopProcessingAsync(CancellationToken.None);
            logger.LogInformation("Service Bus processor stopped");
        }
    }

    internal async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        try
        {
            var body = args.Message.Body.ToString();
            var parsed = JsonSerializer.Deserialize<ServiceBusPromptMessage>(body);

            if (parsed is null || string.IsNullOrEmpty(parsed.Prompt))
            {
                logger.LogWarning("Received invalid message, dead-lettering");
                await args.DeadLetterMessageAsync(args.Message, "InvalidMessage", "Missing required fields");
                return;
            }

            var correlationId = parsed.CorrelationId
                                ?? args.Message.CorrelationId
                                ?? Guid.NewGuid().ToString();
            var sender = parsed.Sender ?? "service-bus";
            // Left null when the sender named nobody, rather than invented here: which agent
            // answers an unattributed prompt is the agent host's configured decision, and a
            // literal "default" is an agent id nothing has ever been able to resolve.
            var agentId = parsed.AgentId;

            // Gate-on-live: a false return means nothing was buffered, so abandoning here hands the
            // prompt back to the broker whole. Settling it instead would defeat at-least-once
            // redelivery for an item the in-process inbox can still lose.
            var delivered = await notificationEmitter.EmitAsync(
                new ChannelMessageNotification
                {
                    ConversationId = correlationId,
                    Sender = sender,
                    Content = parsed.Prompt,
                    AgentId = agentId,
                    Timestamp = DateTimeOffset.UtcNow
                },
                args.CancellationToken);

            if (!delivered)
            {
                logger.LogWarning("No active MCP sessions, abandoning message correlationId={CorrelationId}", correlationId);
                await args.AbandonMessageAsync(args.Message);
                return;
            }

            // Past this point the prompt has been delivered, so the outer catch must not reach the
            // settle: abandoning a delivered prompt hands it back to the broker and redelivery
            // replays it to the agent — the duplicate gate-on-live exists to prevent. A settle that
            // fails (an expired lock, say) is logged and left alone; the broker redelivers of its
            // own accord when the lock runs out, and that path goes through the liveness gate again.
            try
            {
                await args.CompleteMessageAsync(args.Message);
                logger.LogDebug("Processed message correlationId={CorrelationId}", correlationId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Delivered prompt correlationId={CorrelationId} but could not settle the broker message; " +
                    "leaving it for the lock to expire rather than abandoning a delivered prompt",
                    correlationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing Service Bus message");
            await args.AbandonMessageAsync(args.Message);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception,
            "Service Bus processor error: Source={ErrorSource}, Namespace={Namespace}, EntityPath={EntityPath}",
            args.ErrorSource, args.FullyQualifiedNamespace, args.EntityPath);
        return Task.CompletedTask;
    }
}