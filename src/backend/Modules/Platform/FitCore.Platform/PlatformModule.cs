using FitCore.Application.Core;
using FitCore.Infrastructure.Core;
using FitCore.Platform.Application;
using FitCore.Platform.Application.Auth;
using FitCore.Platform.Application.Roles;
using FitCore.Platform.Application.Tenants;
using FitCore.Platform.Application.Users;
using FitCore.Platform.Identity;
using FitCore.Platform.Persistence;
using FitCore.Platform.Security;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Platform;

public static class PlatformModule
{
    /// <summary>Acceso a datos del núcleo (sin identidad ni casos de uso). Lo usa también el Worker.</summary>
    public static IServiceCollection AddPlatformPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddScoped<AuditSaveChangesInterceptor>();

        services.AddDbContext<PlatformDbContext>((provider, options) =>
            options
                .UseNpgsql(configuration.GetConnectionString("Default"))
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
                .AddInterceptors(
                    provider.GetRequiredService<TenantConnectionInterceptor>(),
                    provider.GetRequiredService<AuditSaveChangesInterceptor>()));

        return services;
    }

    public static IServiceCollection AddPlatformModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPlatformPersistence(configuration);

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(o => o.SigningKey.Length >= 32, "Jwt:SigningKey debe tener al menos 32 caracteres.")
            .ValidateOnStart();
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.Configure<PlatformBootstrapOptions>(configuration.GetSection(PlatformBootstrapOptions.SectionName));

        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<PlatformDbContext>();

        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAccessContext, AccessContext>();
        services.AddScoped<SessionService>();
        services.AddScoped<SecurityAudit>();
        services.AddScoped<FitCore.Platform.Application.Branding.TenantBrandingService>();
        services.AddScoped<FitCore.Application.Core.ITenantBranding, FitCore.Platform.Application.Branding.TenantBrandingProvider>();
        services.AddScoped<UserAdministration>();
        services.AddScoped<IMemberAccountService, MemberAccountService>();
        services.AddScoped<RoleAdministration>();
        services.AddScoped<TenantProvisioningService>();
        services.AddEmailQueue(configuration);

        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>(includeInternalTypes: true);
        services.AddHandlersFromAssembly(typeof(PlatformModule).Assembly);

        return services;
    }
}

public static class EmailQueueRegistration
{
    /// <summary>Cola de correo, tokens de cuenta y dirección pública. La API los usa para encolar; el Worker, para enviar.</summary>
    public static IServiceCollection AddEmailQueue(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FitCore.Platform.Application.Registration.LegalOptions>(
            configuration.GetSection(FitCore.Platform.Application.Registration.LegalOptions.SectionName));
        services.Configure<FitCore.Platform.Application.Email.PublicSiteOptions>(
            configuration.GetSection(FitCore.Platform.Application.Email.PublicSiteOptions.SectionName));
        services.AddScoped<FitCore.Platform.Application.Email.EmailQueue>();
        services.AddScoped<FitCore.Platform.Application.AccountTokens.AccountTokenService>();
        return services;
    }
}
