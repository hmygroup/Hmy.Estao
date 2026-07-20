using Hmy.Estao.App;
using Hmy.Estao.Core;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Refresh;

ApplicationConfiguration.Initialize();
Application.SetHighDpiMode(HighDpiMode.SystemAware);

using var context = new TrayApplicationContext(
    new ConfigStore(),
    serviceFactory: store => new UsageRefreshService(store));
Application.Run(context);
