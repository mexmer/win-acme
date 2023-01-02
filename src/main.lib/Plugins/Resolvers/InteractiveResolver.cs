﻿using Autofac;
using PKISharp.WACS.Configuration.Arguments;
using PKISharp.WACS.DomainObjects;
using PKISharp.WACS.Extensions;
using PKISharp.WACS.Plugins.Base.Factories.Null;
using PKISharp.WACS.Plugins.CsrPlugins;
using PKISharp.WACS.Plugins.Interfaces;
using PKISharp.WACS.Plugins.OrderPlugins;
using PKISharp.WACS.Plugins.StorePlugins;
using PKISharp.WACS.Plugins.TargetPlugins;
using PKISharp.WACS.Plugins.ValidationPlugins.Http;
using PKISharp.WACS.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Single = PKISharp.WACS.Plugins.OrderPlugins.Single;

namespace PKISharp.WACS.Plugins.Resolvers
{
    public class InteractiveResolver : UnattendedResolver
    {
        private readonly IPluginService _plugins;
        private readonly ILogService _log;
        private readonly IInputService _input;
        private readonly RunLevel _runLevel;

        public InteractiveResolver(
            ILogService log,
            IInputService inputService,
            ISettingsService settings,
            MainArguments arguments,
            IPluginService pluginService,
            RunLevel runLevel)
            : base(log, settings, arguments, pluginService)
        {
            _log = log;
            _input = inputService;
            _plugins = pluginService;
            _runLevel = runLevel;
        }

        private async Task<Plugin?> GetPlugin<T>(
            ILifetimeScope scope,
            Steps step,
            Type defaultType,
            Type defaultTypeFallback,
            string className,
            string shortDescription,
            string longDescription,
            string? defaultParam1 = null,
            string? defaultParam2 = null,
            Func<IEnumerable<PluginFactoryContext<T>>, IEnumerable<PluginFactoryContext<T>>>? sort = null, 
            Func<IEnumerable<PluginFactoryContext<T>>, IEnumerable<PluginFactoryContext<T>>>? filter = null,
            Func<PluginFactoryContext<T>, (bool, string?)>? unusable = null,
            Func<PluginFactoryContext<T>, string>? description = null,
            bool allowAbort = true) where T : class, IPluginOptionsFactory
        {
            // Helper method to determine final usability state
            // combination of plugin being enabled (e.g. due to missing
            // administrator rights) and being a right fit for the current
            // renewal (e.g. cannot validate wildcards using http-01)
            (bool, string?) disabledOrUnusable(PluginFactoryContext<T> plugin)
            {
                var disabled = plugin.Factory.Disabled;
                if (disabled.Item1)
                {
                    return disabled;
                }
                else if (unusable != null)
                {
                    return unusable(plugin);
                }
                return (false, null);
            };

            // Apply default sorting when no sorting has been provided yet
            IEnumerable<PluginFactoryContext<T>> options = new List<PluginFactoryContext<T>>();
            options = _plugins.
                GetPlugins(step).
                Where(x => !x.Hidden).
                Select(x => new PluginFactoryContext<T>(x, scope)).
                ToList();
            options = filter != null ? filter(options) : options.Where(x => x is not INull);
            options = sort != null ? sort(options) : options.OrderBy(x => x.Factory.Order).ThenBy(x => x.Meta.Description);
            var localOptions = options.Select(x => new {
                plugin = x,
                disabled = disabledOrUnusable(x)
            });

            // Default out when there are no reasonable options to pick
            if (!localOptions.Any() ||
                localOptions.All(x => x.disabled.Item1) ||
                localOptions.All(x => x.plugin.Factory is INull))
            {
                return null;
            }

            // Always show the menu in advanced mode, only when no default
            // selection can be made in simple mode
            var showMenu = _runLevel.HasFlag(RunLevel.Advanced);
            if (!string.IsNullOrEmpty(defaultParam1))
            {
                var defaultPlugin = _plugins.GetPlugin(step, defaultParam1, defaultParam2);
                if (defaultPlugin != null)
                {
                    defaultType = defaultPlugin.Runner;
                } 
                else
                {
                    _log.Error("Unable to find {n} plugin {p}", className, defaultParam1);
                    showMenu = true;
                }
            }

            var defaultOption = localOptions.FirstOrDefault(x => x.plugin.Meta.Runner == defaultType);
            var defaultTypeDisabled = defaultOption?.disabled ?? (true, "Not found");
            if (defaultTypeDisabled.Item1)
            {
                _log.Warning("{n} plugin {x} not available: {m}",
                    char.ToUpper(className[0]) + className[1..],
                    defaultOption?.plugin.Meta.Name ?? defaultType.Name, 
                    defaultTypeDisabled.Item2);
                defaultType = defaultTypeFallback;
                showMenu = true;
            }

            if (!showMenu)
            {
                return defaultOption?.plugin.Meta;
            }

            // List options for generating new certificates
            if (!string.IsNullOrEmpty(longDescription))
            {
                _input.CreateSpace();
                _input.Show(null, longDescription);
            }

            Choice<PluginFactoryContext<T>?> creator(PluginFactoryContext<T> plugin, (bool, string?) disabled) {
                return Choice.Create<PluginFactoryContext<T>?>(
                       plugin,
                       description: description == null ? plugin.Meta.Description : description(plugin),
                       @default: plugin.Meta.Runner == defaultType && !disabled.Item1,
                       disabled: disabled);
            }

            var ret = allowAbort
                ? await _input.ChooseOptional(
                    shortDescription,
                    localOptions,
                    x => creator(x.plugin, x.disabled),
                    "Abort")
                : await _input.ChooseRequired(
                    shortDescription,
                    localOptions,
                    x => creator(x.plugin, x.disabled));

            return ret?.Meta;
        }

        /// <summary>
        /// Allow user to choose a TargetPlugin
        /// </summary>
        /// <returns></returns>
        public override async Task<Plugin?> GetTargetPlugin(ILifetimeScope scope)
        {
            return await GetPlugin<ITargetPluginOptionsFactory>(
                scope,
                Steps.Target,
                defaultParam1: _settings.Source.DefaultSource,
                defaultType: typeof(IIS),
                defaultTypeFallback: typeof(Manual),
                className: "source",
                shortDescription: "How shall we determine the domain(s) to include in the certificate?",
                longDescription: "Please specify how the list of domain names that will be included in the certificate " +
                    "should be determined. If you choose for one of the \"all bindings\" options, the list will automatically be " +
                    "updated for future renewals to reflect the bindings at that time.");
        }

        /// <summary>
        /// Allow user to choose a ValidationPlugin
        /// </summary>
        /// <returns></returns>
        public override async Task<Plugin?> GetValidationPlugin(ILifetimeScope scope, Target target)
        {
            var defaultParam1 = _settings.Validation.DefaultValidation;
            var defaultParam2 = _settings.Validation.DefaultValidationMode;
            if (string.IsNullOrEmpty(defaultParam2))
            {
                defaultParam2 = Constants.Http01ChallengeType;
            }
            if (!string.IsNullOrWhiteSpace(_arguments.Validation))
            {
                defaultParam1 = _arguments.Validation;
            }
            if (!string.IsNullOrWhiteSpace(_arguments.ValidationMode))
            {
                defaultParam2 = _arguments.ValidationMode;
            }
            return await GetPlugin<IValidationPluginOptionsFactory>(
                scope,
                Steps.Validation,
                sort: x =>
                    x.
                        OrderBy(x =>
                        {
                            return x.Meta.ChallengeType switch
                            {
                                Constants.Http01ChallengeType => 0,
                                Constants.Dns01ChallengeType => 1,
                                Constants.TlsAlpn01ChallengeType => 2,
                                _ => 3,
                            };
                        }).
                        ThenBy(x => x.Factory.Order).
                        ThenBy(x => x.Meta.Description),
                unusable: x => (!x.Factory.CanValidate(target), "Unsuppored target. Most likely this is because you have included a wildcard identifier (*.example.com), which requires DNS validation."),
                description: x => $"[{x.Meta.ChallengeType}] {x.Meta.Description}",
                defaultParam1: defaultParam1,
                defaultParam2: defaultParam2,
                defaultType: typeof(SelfHosting),
                defaultTypeFallback: typeof(FileSystem),
                className: "validation",
                shortDescription: "How would you like prove ownership for the domain(s)?",
                longDescription: "The ACME server will need to verify that you are the owner of the domain names that you are requesting" +
                    " the certificate for. This happens both during initial setup *and* for every future renewal. There are two main methods of doing so: " +
                    "answering specific http requests (http-01) or create specific dns records (dns-01). For wildcard domains the latter is the only option. " +
                    "Various additional plugins are available from https://github.com/win-acme/win-acme/.");
        }

        public override async Task<Plugin?> GetOrderPlugin(ILifetimeScope scope, Target target)
        {
            if (target.Parts.SelectMany(x => x.Identifiers).Count() > 1)
            {
                return await GetPlugin<IOrderPluginOptionsFactory>(
                   scope,
                   Steps.Order,
                   defaultParam1: _settings.Order.DefaultPlugin,
                   defaultType: typeof(Single),
                   defaultTypeFallback: typeof(Single),
                   unusable: (c) => (!c.Factory.CanProcess(target), "Unsupported source."),
                   className: "order",
                   shortDescription: "Would you like to split this source into multiple certificates?",
                   longDescription: $"By default your source hosts are covered by a single certificate. " +
                        $"But if you want to avoid the {Constants.MaxNames} domain limit, want to prevent " +
                        $"information disclosure via the SAN list, and/or reduce the impact of a single validation failure," +
                        $"you may choose to convert one source into multiple certificates, using different strategies.");
            } 
            else
            {
                return await base.GetOrderPlugin(scope, target);
            }
        }

        public override async Task<Plugin?> GetCsrPlugin(ILifetimeScope scope)
        {
            return await GetPlugin<ICsrPluginOptionsFactory>(
               scope,
               Steps.Csr,
               defaultParam1: _settings.Csr.DefaultCsr,
               defaultType: typeof(Rsa),
               defaultTypeFallback: typeof(Ec),
               className: "csr",
               shortDescription: "What kind of private key should be used for the certificate?",
               longDescription: "After ownership of the domain(s) has been proven, we will create a " +
                "Certificate Signing Request (CSR) to obtain the actual certificate. The CSR " +
                "determines properties of the certificate like which (type of) key to use. If you " +
                "are not sure what to pick here, RSA is the safe default.");
        }

        public override async Task<Plugin?> GetStorePlugin(ILifetimeScope scope, IEnumerable<Plugin> chosen)
        {
            var defaultType = typeof(CertificateStore);
            var shortDescription = "How would you like to store the certificate?";
            var longDescription = "When we have the certificate, you can store in one or more ways to make it accessible " +
                        "to your applications. The Windows Certificate Store is the default location for IIS (unless you are " +
                        "managing a cluster of them).";
            if (chosen.Any())
            {
                if (!_runLevel.HasFlag(RunLevel.Advanced))
                {
                    return null;
                }
                longDescription = "";
                shortDescription = "Would you like to store it in another way too?";
                defaultType = typeof(NullStore);
            }
            var defaultParam1 = _settings.Store.DefaultStore;
            if (!string.IsNullOrWhiteSpace(_arguments.Store))
            {
                defaultParam1 = _arguments.Store;
            }
            var csv = defaultParam1.ParseCsv();
            defaultParam1 = csv?.Count > chosen.Count() ? 
                csv[chosen.Count()] : 
                "";
            return await GetPlugin<IStorePluginOptionsFactory>(
                scope,
                Steps.Store,
                filter: (x) => x, // Disable default null check
                defaultParam1: defaultParam1,
                defaultType: defaultType,
                defaultTypeFallback: typeof(PemFiles),
                className: "store",
                shortDescription: shortDescription,
                longDescription: longDescription,
                allowAbort: false);
        }

        /// <summary>
        /// Allow user to choose a InstallationPlugins
        /// </summary>
        /// <returns></returns>
        /// <summary>
        /// </summary>
        /// <returns></returns>
        public override async Task<Plugin?> GetInstallationPlugin(ILifetimeScope scope, IEnumerable<Plugin> storeTypes, IEnumerable<Plugin> chosen)
        {
            var defaultType = typeof(InstallationPlugins.IIS);
            var shortDescription = "Which installation step should run first?";
            var longDescription = "With the certificate saved to the store(s) of your choice, " +
                "you may choose one or more steps to update your applications, e.g. to configure " +
                "the new thumbprint, or to update bindings.";
            if (chosen.Any())
            {
                if (!_runLevel.HasFlag(RunLevel.Advanced))
                {
                    return null;
                }
                longDescription = "";
                shortDescription = "Add another installation step?";
                defaultType = typeof(NullInstallation);
            }
            var defaultParam1 = _settings.Installation.DefaultInstallation;
            if (!string.IsNullOrWhiteSpace(_arguments.Installation))
            {
                defaultParam1 = _arguments.Installation;
            }
            var csv = defaultParam1.ParseCsv();
            defaultParam1 = csv?.Count > chosen.Count() ?
                csv[chosen.Count()] :
                "";
            return await GetPlugin<IInstallationPluginOptionsFactory>(
                scope,
                Steps.Installation,
                filter: x => x, // Disable default null check
                unusable: x => { var (a, b) = x.Factory.CanInstall(storeTypes.Select(c => c.Runner), chosen.Select(c => c.Runner)); return (!a, b); },
                defaultParam1: defaultParam1,
                defaultType: defaultType,
                defaultTypeFallback: typeof(NullInstallation),
                className: "installation",
                shortDescription: shortDescription,
                longDescription: longDescription,
                allowAbort: false);
        }
    }
}
