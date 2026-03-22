using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.DependencyInjection;

/// <summary>
/// Generic logging interceptor using DispatchProxy (built-in .NET, zero NuGet).
///
/// Automatically logs ENTER / EXIT / FAILED for every method call on a service interface.
/// Applied transparently via DI — the service implementation has no idea it's being intercepted.
///
/// Log output format:
///   [INF] InvoiceService.CreateInvoiceAsync → ENTER
///   [INF] InvoiceService.CreateInvoiceAsync → EXIT (45ms)
///   [ERR] InvoiceService.CreateInvoiceAsync → FAILED (12ms): InvalidOperationException: ...
///
/// For async methods (returning Task or Task&lt;T&gt;), the proxy awaits the task
/// and measures the full async duration (not just the synchronous part).
///
/// Junior note: DispatchProxy creates a runtime subclass of the interface that forwards
/// all method calls through the Invoke method. It's like a "man in the middle" for DI.
/// </summary>
/// <typeparam name="TInterface">The service interface being proxied.</typeparam>
public class LoggingProxy<TInterface> : DispatchProxy where TInterface : class
{
    // These are set by the Create method via reflection (DispatchProxy limitation).
    private TInterface _target = null!;
    private ILogger _logger = null!;
    private string _serviceName = null!;

    /// <summary>
    /// Creates a logging proxy wrapping the given target implementation.
    /// </summary>
    public static TInterface Create(TInterface target, ILogger logger)
    {
        // DispatchProxy.Create returns a proxy instance that implements TInterface.
        // All method calls on this instance go through Invoke().
        var proxy = Create<TInterface, LoggingProxy<TInterface>>() as LoggingProxy<TInterface>;
        proxy!._target = target;
        proxy._logger = logger;
        proxy._serviceName = typeof(TInterface).Name.TrimStart('I'); // IInvoiceService → InvoiceService
        return (proxy as TInterface)!;
    }

    /// <summary>
    /// Intercepts every method call on the proxied interface.
    /// Logs ENTER, measures elapsed time, logs EXIT or FAILED.
    /// Handles both sync and async methods transparently.
    /// </summary>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            return null;

        var methodName = targetMethod.Name;

        // Skip property getters/setters and ToString/GetHashCode — too noisy.
        if (methodName.StartsWith("get_") || methodName.StartsWith("set_") ||
            methodName == "ToString" || methodName == "GetHashCode" || methodName == "Equals")
        {
            return targetMethod.Invoke(_target, args);
        }

        var returnType = targetMethod.ReturnType;

        // Check if the method returns Task or Task<T> (async method).
        if (typeof(Task).IsAssignableFrom(returnType))
        {
            return InvokeAsync(targetMethod, args, returnType);
        }

        // Synchronous method — measure and log directly.
        return InvokeSync(targetMethod, args);
    }

    /// <summary>
    /// Handles synchronous method calls (rare in this codebase, but supported).
    /// </summary>
    private object? InvokeSync(MethodInfo method, object?[]? args)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Service}.{Method} → ENTER", _serviceName, method.Name);

        try
        {
            var result = method.Invoke(_target, args);
            sw.Stop();
            _logger.LogInformation("{Service}.{Method} → EXIT ({Elapsed}ms)",
                _serviceName, method.Name, sw.ElapsedMilliseconds);
            return result;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            sw.Stop();
            _logger.LogError(ex.InnerException,
                "{Service}.{Method} → FAILED ({Elapsed}ms): {Error}",
                _serviceName, method.Name, sw.ElapsedMilliseconds, ex.InnerException.Message);
            throw ex.InnerException;
        }
    }

    /// <summary>
    /// Handles async method calls (Task or Task&lt;T&gt;).
    /// Must return a Task of the correct type so the caller can await it.
    /// </summary>
    private object? InvokeAsync(MethodInfo method, object?[]? args, Type returnType)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("{Service}.{Method} → ENTER", _serviceName, method.Name);

        try
        {
            var resultTask = method.Invoke(_target, args);

            if (resultTask == null)
                return null;

            // Task<T> — need to wrap with continuation that logs and returns the correct type.
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                // Use reflection to call WrapTaskWithResult<T> with the correct generic argument.
                var resultType = returnType.GetGenericArguments()[0];
                var wrapMethod = typeof(LoggingProxy<TInterface>)
                    .GetMethod(nameof(WrapTaskWithResult), BindingFlags.NonPublic | BindingFlags.Instance)!
                    .MakeGenericMethod(resultType);

                return wrapMethod.Invoke(this, [resultTask, method.Name, sw]);
            }

            // Plain Task (no return value).
            return WrapTask((Task)resultTask, method.Name, sw);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            sw.Stop();
            _logger.LogError(ex.InnerException,
                "{Service}.{Method} → FAILED ({Elapsed}ms): {Error}",
                _serviceName, method.Name, sw.ElapsedMilliseconds, ex.InnerException.Message);
            throw ex.InnerException;
        }
    }

    /// <summary>
    /// Wraps a plain Task with logging on completion/failure.
    /// </summary>
    private async Task WrapTask(Task task, string methodName, Stopwatch sw)
    {
        try
        {
            await task;
            sw.Stop();
            _logger.LogDebug("{Service}.{Method} → EXIT ({Elapsed}ms)",
                _serviceName, methodName, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "{Service}.{Method} → FAILED ({Elapsed}ms): {Error}",
                _serviceName, methodName, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Wraps a Task&lt;T&gt; with logging on completion/failure.
    /// The generic parameter ensures the return type matches the original method.
    /// </summary>
    // ReSharper disable once UnusedMember.Local — called via reflection in InvokeAsync.
    private async Task<T> WrapTaskWithResult<T>(object taskObj, string methodName, Stopwatch sw)
    {
        try
        {
            var result = await (Task<T>)taskObj;
            sw.Stop();
            _logger.LogDebug("{Service}.{Method} → EXIT ({Elapsed}ms)",
                _serviceName, methodName, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "{Service}.{Method} → FAILED ({Elapsed}ms): {Error}",
                _serviceName, methodName, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }
}

/// <summary>
/// Extension methods for registering services with automatic logging proxy.
///
/// Usage:
///   services.AddScopedWithLogging&lt;IInvoiceService, InvoiceService&gt;();
///
/// This registers InvoiceService normally, then wraps it in LoggingProxy
/// so all interface method calls are automatically logged with ENTER/EXIT/FAILED.
/// </summary>
public static class LoggingProxyExtensions
{
    /// <summary>
    /// Registers a scoped service with a transparent logging proxy.
    /// The proxy intercepts all method calls and logs ENTER/EXIT/FAILED with elapsed time.
    /// </summary>
    public static IServiceCollection AddScopedWithLogging<TInterface, TImplementation>(
        this IServiceCollection services)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        // Register the real implementation as itself (so DI can resolve it).
        services.AddScoped<TImplementation>();

        // Register the interface → logging proxy that wraps the real implementation.
        services.AddScoped<TInterface>(sp =>
        {
            var target = sp.GetRequiredService<TImplementation>();
            var logger = sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger($"Fakvio.Service.{typeof(TInterface).Name.TrimStart('I')}");
            return LoggingProxy<TInterface>.Create(target, logger);
        });

        return services;
    }
}
