using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Kharos.Plugins.Abstractions;
using Kharos.Plugins.Manager;
using Kharos.Plugins.Runtime;

namespace Kharos.Plugins.DependencyInjection;

/// <summary>
/// Построитель конфигурации системы плагинов WebAssembly для регистрации возможностей, логгирования и хост-функций.
/// </summary>
public interface IWasmPluginSystemBuilder
{
    /// <summary>
    /// Коллекция сервисов внедрения зависимостей.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Добавляет безопасную функцию хоста в компоновщик Linker для вызова из плагинов.
    /// </summary>
    /// <param name="registerAction">Действие регистрации сигнатуры функции в <see cref="Wasmtime.Linker"/>.</param>
    /// <returns>Текущий экземпляр построителя для цепочки вызовов.</returns>
    IWasmPluginSystemBuilder AddHostCapability(Action<Wasmtime.Linker> registerAction);

    /// <summary>
    /// Настраивает провайдеры и параметры логирования Microsoft.Extensions.Logging для системы плагинов.
    /// </summary>
    /// <param name="configureLogging">Делегат настройки <see cref="ILoggingBuilder"/>.</param>
    /// <returns>Текущий экземпляр построителя для цепочки вызовов.</returns>
    IWasmPluginSystemBuilder ConfigureLogging(Action<ILoggingBuilder> configureLogging);
}

/// <summary>
/// Реализация построителя подсистемы плагинов WebAssembly.
/// </summary>
public sealed class WasmPluginSystemBuilder(IServiceCollection services, HostCapabilityRegistry capabilities)
    : IWasmPluginSystemBuilder
{
    /// <inheritdoc/>
    public IServiceCollection Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    private readonly HostCapabilityRegistry _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));

    /// <inheritdoc/>
    public IWasmPluginSystemBuilder AddHostCapability(Action<Wasmtime.Linker> registerAction)
    {
        ArgumentNullException.ThrowIfNull(registerAction);
        _capabilities.AddCustom(registerAction);
        return this;
    }

    /// <inheritdoc/>
    public IWasmPluginSystemBuilder ConfigureLogging(Action<ILoggingBuilder> configureLogging)
    {
        ArgumentNullException.ThrowIfNull(configureLogging);
        Services.AddLogging(configureLogging);
        return this;
    }
}

/// <summary>
/// Методы расширения <see cref="IServiceCollection"/> для регистрации компонентов системы WebAssembly плагинов.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует промышленную систему плагинов WebAssembly в контейнере внедрения зависимостей.
    /// </summary>
    /// <param name="services">Коллекция сервисов приложения.</param>
    /// <param name="configure">Делегат настройки параметров <see cref="WasmPluginOptions"/>.</param>
    /// <returns>Построитель <see cref="IWasmPluginSystemBuilder"/> для дальнейшей настройки возможностей хоста.</returns>
    /// <exception cref="ArgumentNullException">Выбрасывается, если <paramref name="services"/> равен <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// var services = new ServiceCollection();
    /// services.AddWasmPluginSystem(options =>
    /// {
    ///     options.WatchdogIntervalMs = 10;
    ///     options.DefaultMaxPoolSize = 32;
    /// })
    /// .ConfigureLogging(log => log.AddConsole())
    /// .AddHostCapability(linker =>
    /// {
    ///     linker.DefineFunction("env", "custom_log", (int msg) => Console.WriteLine(msg));
    /// });
    /// </code>
    /// </example>
    public static IWasmPluginSystemBuilder AddWasmPluginSystem(
        this IServiceCollection services,
        Action<WasmPluginOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new WasmPluginOptions();
        configure?.Invoke(options);
        options.Validate();

        var capabilities = new HostCapabilityRegistry();

        services.AddSingleton(options);
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<ILogger<HostCapabilityRegistry>>();
            if (logger != null)
            {
                capabilities.AttachLogger(logger);
            }
            return capabilities;
        });
        services.AddSingleton<IPluginManager>(sp =>
        {
            var opt = sp.GetRequiredService<WasmPluginOptions>();
            var caps = sp.GetRequiredService<HostCapabilityRegistry>();
            var logger = sp.GetService<ILogger<WasmPluginManager>>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new WasmPluginManager(caps, opt.WatchdogIntervalMs, logger, loggerFactory);
        });

        return new WasmPluginSystemBuilder(services, capabilities);
    }
}
