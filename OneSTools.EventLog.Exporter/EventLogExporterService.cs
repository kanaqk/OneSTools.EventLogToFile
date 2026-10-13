using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneSTools.EventLog.Exporter.Core;

namespace OneSTools.EventLog.Exporter
{
    public class EventLogExporterService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<EventLogExporterService> _logger;
        private bool _disposedValue;
        private readonly bool _loadArchive;
        private readonly IHostApplicationLifetime _lifetime;

        public EventLogExporterService(IServiceProvider serviceProvider, ILogger<EventLogExporterService> logger,
            IConfiguration configuration, IHostApplicationLifetime lifetime)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _loadArchive = configuration.GetValue("Exporter:LoadArchive", false);
            _lifetime = lifetime;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var exporter = _serviceProvider.GetService<EventLogExporter>();
                    await exporter.StartAsync(stoppingToken);
                    if (_loadArchive)
                    {
                        _lifetime.StopApplication();
                        return;
                    }
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.LogCritical(ex, "Failed to execute EventLogExporter");
                }
                await Task.Delay(5000);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                }

                _disposedValue = true;
            }
        }

        ~EventLogExporterService()
        {
            Dispose(false);
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
