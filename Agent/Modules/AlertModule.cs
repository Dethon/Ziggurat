using Agent.App;
using Domain.Contracts;
using Domain.Monitor;
using Infrastructure.Timers;

namespace Agent.Modules;

public static class AlertModule
{
    extension(IServiceCollection services)
    {
        // An insistent alert is rung on every channel connection that can show one, so the relay is
        // handed the connections the chat monitor holds, live: the timers server's raises the alert,
        // and the chat client's and Telegram's show it.
        public IServiceCollection AddAlertRinging() =>
            services
                .AddSingleton(sp => new AlertRelay(
                    sp.GetRequiredService<IConnectionSet<IChannelConnection>>().As<IAlertChannel>(),
                    new RingingAlerts(),
                    sp.GetRequiredService<ILogger<AlertRelay>>(),
                    sp.GetRequiredService<TimeProvider>()))
                .AddHostedService<AlertRinging>();
    }
}