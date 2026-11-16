using Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Extensions;

// The one CORS policy the agent and the hub share. Both answer browsers with a visitor's
// credentials, so an origin outside the deployment's own list gets no CORS headers and its page
// cannot read a reply; an empty list is same-origin only. A listed "*" is refused by the framework
// itself (credentials with any origin), which is the right answer: it is what this policy forbids.
public static class CorsExtensions
{
    public static IServiceCollection AddDeploymentCors(this IServiceCollection services, CorsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(settings.AllowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()));
    }
}