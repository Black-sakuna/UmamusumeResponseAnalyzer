using System.Reflection;
using System.Runtime.CompilerServices;
using Gallop;
using Gallop.Endpoints;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests
{
    public sealed class PluginAbiBoundaryTests
    {
        const BindingFlags PublicDeclared =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        [Fact]
        public void PluginContractsAndGallopModelsComeFromHostAssembly()
        {
            var hostAssembly = typeof(Server).Assembly;

            Assert.Same(hostAssembly, typeof(IPlugin).Assembly);
            Assert.Same(hostAssembly, typeof(AnalyzerAttribute).Assembly);
            Assert.Same(hostAssembly, typeof(Workspace).Assembly);
            Assert.Same(hostAssembly, typeof(WorkspaceContent).Assembly);
            Assert.Same(hostAssembly, typeof(UiShortcut).Assembly);
            Assert.Same(hostAssembly, typeof(UiSeverity).Assembly);
            Assert.Same(hostAssembly, typeof(TerminalUi).Assembly);
            Assert.Same(hostAssembly, typeof(HotkeyManager).Assembly);
            Assert.Same(hostAssembly, typeof(HotkeyContext).Assembly);
            Assert.Same(hostAssembly, typeof(IGameEndpoint).Assembly);
            Assert.Same(hostAssembly, typeof(DataLinkIndexResponse).Assembly);
            Assert.Null(hostAssembly.GetType("UmamusumeResponseAnalyzer.Game.TurnInfo.SingleModeTurnData"));
            Assert.Contains(hostAssembly.GetTypes(), type =>
                type.Namespace == "Gallop" ||
                type.Namespace?.StartsWith("Gallop.", StringComparison.Ordinal) == true);
        }

        [Fact]
        public void WorkspacePublicApiMatchesTargetManifest()
        {
            var type = typeof(Workspace);

            Assert.True(type.IsPublic);
            Assert.True(type.IsClass);
            Assert.True(type.IsSealed);
            Assert.False(type.IsAbstract);
            Assert.Equal(typeof(object), type.BaseType);
            Assert.Empty(type.GetInterfaces());
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.Empty(type.GetEvents(PublicDeclared));
            Assert.Empty(type.GetFields(PublicDeclared));

            var properties = type.GetProperties(PublicDeclared).ToDictionary(property => property.Name);
            Assert.Equal(
                new[] { "Current", "Title" },
                properties.Keys.Order(StringComparer.Ordinal).ToArray());
            foreach (var (name, propertyType, isStatic) in new[]
            {
                ("Current", typeof(Workspace), true),
                ("Title", typeof(string), false)
            })
            {
                Assert.Equal(propertyType, properties[name].PropertyType);
                Assert.Equal(isStatic, properties[name].GetMethod!.IsStatic);
                Assert.Null(properties[name].SetMethod);
            }

            var methods = type.GetMethods(PublicDeclared)
                .Where(method => !method.IsSpecialName)
                .ToDictionary(method => method.Name);
            Assert.Equal(
                new[] { "get_Current", "get_Title" },
                type.GetMethods(PublicDeclared)
                    .Where(method => method.IsSpecialName)
                    .Select(method => method.Name)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
            Assert.Equal(
                new[]
                {
                    "BindHotkey",
                    "Create",
                    "Notify",
                    "Remove",
                    "RemovePanel",
                    "SetPanel",
                    "SwitchTo"
                },
                methods.Keys.Order(StringComparer.Ordinal).ToArray());
            foreach (var (name, isStatic, returnType, parameterCount) in new[]
            {
                ("BindHotkey", false, typeof(void), 3),
                ("Create", true, typeof(Workspace), 1),
                ("Notify", false, typeof(void), 4),
                ("Remove", false, typeof(void), 0),
                ("RemovePanel", false, typeof(bool), 1),
                ("SetPanel", false, typeof(void), 5),
                ("SwitchTo", false, typeof(void), 0)
            })
            {
                Assert.Equal(isStatic, methods[name].IsStatic);
                Assert.Equal(returnType, methods[name].ReturnType);
                Assert.Equal(parameterCount, methods[name].GetParameters().Length);
            }

            AssertParameter(Assert.Single(methods["Create"].GetParameters()), "title", typeof(string));

            var setPanelParameters = methods["SetPanel"].GetParameters();
            AssertParameter(setPanelParameters[0], "key", typeof(string));
            AssertParameter(setPanelParameters[1], "title", typeof(string));
            AssertParameter(setPanelParameters[2], "content", typeof(WorkspaceContent));
            AssertParameter(setPanelParameters[3], "fullBleed", typeof(bool), false);
            AssertParameter(setPanelParameters[4], "switchToWorkspace", typeof(bool), true);

            AssertParameter(
                Assert.Single(methods["RemovePanel"].GetParameters()),
                "key",
                typeof(string));

            var notifyParameters = methods["Notify"].GetParameters();
            AssertParameter(notifyParameters[0], "text", typeof(string));
            AssertParameter(notifyParameters[1], "severity", typeof(UiSeverity), UiSeverity.Info);
            AssertParameter(notifyParameters[2], "ttl", typeof(TimeSpan?), null);
            AssertParamsParameter(notifyParameters[3], "shortcuts", typeof(UiShortcut[]));

            var bindHotkeyParameters = methods["BindHotkey"].GetParameters();
            AssertParameter(bindHotkeyParameters[0], "key", typeof(ConsoleKey));
            AssertParameter(bindHotkeyParameters[1], "modifiers", typeof(ConsoleModifiers), (ConsoleModifiers)0);
            AssertParameter(bindHotkeyParameters[2], "description", typeof(string), null);
        }

        [Fact]
        public void WorkspaceSupportingTypesMatchTargetManifest()
        {
            var contentType = typeof(WorkspaceContent);
            var shortcutType = typeof(UiShortcut);
            foreach (var type in new[] { contentType, shortcutType })
            {
                Assert.True(type.IsPublic);
                Assert.True(type.IsClass);
                Assert.True(type.IsSealed);
                Assert.False(type.IsAbstract);
                Assert.Equal(typeof(object), type.BaseType);
                Assert.Empty(type.GetEvents(PublicDeclared));
                Assert.Empty(type.GetFields(PublicDeclared));
            }
            Assert.Empty(contentType.GetInterfaces());
            Assert.Equal(new[] { typeof(IEquatable<UiShortcut>) }, shortcutType.GetInterfaces());
            Assert.Empty(contentType.GetProperties(PublicDeclared));

            var contentConstructor = Assert.Single(contentType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            AssertParameter(
                Assert.Single(contentConstructor.GetParameters()),
                "createView",
                typeof(Func<View>));

            var contentMethods = contentType.GetMethods(PublicDeclared)
                .Where(method => !method.IsSpecialName)
                .ToDictionary(method => method.Name);
            Assert.Equal(
                new[] { "CreateView", "Text" },
                contentMethods.Keys.Order(StringComparer.Ordinal).ToArray());
            foreach (var (name, isStatic, returnType, parameterCount) in new[]
            {
                ("CreateView", false, typeof(View), 0),
                ("Text", true, typeof(WorkspaceContent), 1)
            })
            {
                Assert.Equal(isStatic, contentMethods[name].IsStatic);
                Assert.Equal(returnType, contentMethods[name].ReturnType);
                Assert.Equal(parameterCount, contentMethods[name].GetParameters().Length);
            }
            AssertParameter(
                Assert.Single(contentMethods["Text"].GetParameters()),
                "text",
                typeof(string));

            var shortcutConstructor = Assert.Single(shortcutType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            var shortcutConstructorParameters = shortcutConstructor.GetParameters();
            Assert.Equal(3, shortcutConstructorParameters.Length);
            AssertParameter(shortcutConstructorParameters[0], "Key", typeof(ConsoleKey));
            AssertParameter(shortcutConstructorParameters[1], "Handler", typeof(Func<Task>));
            AssertParameter(shortcutConstructorParameters[2], "Modifiers", typeof(ConsoleModifiers), (ConsoleModifiers)0);

            var shortcutProperties = shortcutType.GetProperties(PublicDeclared);
            Assert.Equal(
                new[] { "Handler", "Key", "Modifiers" },
                shortcutProperties.Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
            AssertReadWriteProperty(shortcutProperties, "Key", typeof(ConsoleKey));
            AssertReadWriteProperty(shortcutProperties, "Handler", typeof(Func<Task>));
            AssertReadWriteProperty(shortcutProperties, "Modifiers", typeof(ConsoleModifiers));

            var severityType = typeof(UiSeverity);
            Assert.True(severityType.IsPublic);
            Assert.True(severityType.IsEnum);
            Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(UiSeverity)));
            Assert.Equal(
                new[] { "Trace", "Info", "Success", "Warning", "Error" },
                Enum.GetNames<UiSeverity>());
            Assert.Equal(
                new[] { 0, 1, 2, 3, 4 },
                Enum.GetValues<UiSeverity>().Select(value => (int)value).ToArray());
        }

        [Fact]
        public void HotkeyPublicApiMatchesTargetManifest()
        {
            var managerType = typeof(HotkeyManager);
            Assert.True(managerType.IsPublic);
            Assert.True(managerType.IsClass);
            Assert.True(managerType.IsAbstract);
            Assert.True(managerType.IsSealed);
            Assert.Equal(typeof(object), managerType.BaseType);
            Assert.Empty(managerType.GetInterfaces());
            Assert.Empty(managerType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.Empty(managerType.GetEvents(PublicDeclared));
            Assert.Empty(managerType.GetFields(PublicDeclared));

            var managerProperties = managerType.GetProperties(PublicDeclared)
                .ToDictionary(property => property.Name);
            Assert.Equal(
                new[] { "Hotkeys", "PopupAutoCloseDelay" },
                managerProperties.Keys.Order(StringComparer.Ordinal).ToArray());

            var hotkeys = managerProperties["Hotkeys"];
            Assert.Equal(
                typeof(IReadOnlyDictionary<,>).MakeGenericType(
                    typeof((ConsoleKey Key, ConsoleModifiers Modifiers)),
                    typeof(HotkeyManager.HotkeyEntry)),
                hotkeys.PropertyType);
            Assert.True(hotkeys.GetMethod!.IsStatic);
            Assert.Null(hotkeys.SetMethod);

            var popupAutoCloseDelay = managerProperties["PopupAutoCloseDelay"];
            Assert.Equal(typeof(TimeSpan), popupAutoCloseDelay.PropertyType);
            Assert.True(popupAutoCloseDelay.GetMethod!.IsStatic);
            Assert.True(popupAutoCloseDelay.SetMethod!.IsStatic);

            var managerMethods = managerType.GetMethods(PublicDeclared)
                .Where(method => !method.IsSpecialName)
                .ToArray();
            Assert.Equal(
                new[]
                {
                    "FormatKeyCombo",
                    "Register",
                    "Register",
                    "Register",
                    "Register",
                    "RegisterScope",
                    "Unregister",
                    "UnregisterAll",
                    "UnregisterByOwner"
                },
                managerMethods.Select(method => method.Name).Order(StringComparer.Ordinal).ToArray());

            var registerSignatures = new[]
            {
                (
                    Types: new[] { typeof(ConsoleKey), typeof(ConsoleModifiers), typeof(string), typeof(Func<Task>) },
                    Names: new[] { "key", "modifiers", "description", "handler" }),
                (
                    Types: new[] { typeof(ConsoleKey), typeof(ConsoleModifiers), typeof(string), typeof(Func<HotkeyContext, Task>) },
                    Names: new[] { "key", "modifiers", "description", "handler" }),
                (
                    Types: new[] { typeof(ConsoleKey), typeof(string), typeof(Func<Task>) },
                    Names: new[] { "key", "description", "handler" }),
                (
                    Types: new[] { typeof(ConsoleKey), typeof(string), typeof(Func<HotkeyContext, Task>) },
                    Names: new[] { "key", "description", "handler" })
            };
            foreach (var signature in registerSignatures)
            {
                var method = managerType.GetMethod(
                    "Register",
                    PublicDeclared,
                    binder: null,
                    signature.Types,
                    modifiers: null);
                Assert.NotNull(method);
                Assert.True(method.IsStatic);
                Assert.Equal(typeof(void), method.ReturnType);
                var parameters = method.GetParameters();
                for (var i = 0; i < parameters.Length; i++)
                    AssertParameter(parameters[i], signature.Names[i], signature.Types[i]);
            }

            var otherMethods = managerMethods
                .Where(method => method.Name != "Register")
                .ToDictionary(method => method.Name);
            foreach (var (name, returnType, parameterCount) in new[]
            {
                ("FormatKeyCombo", typeof(string), 2),
                ("RegisterScope", typeof(IDisposable), 1),
                ("Unregister", typeof(bool), 2),
                ("UnregisterAll", typeof(void), 0),
                ("UnregisterByOwner", typeof(int), 1)
            })
            {
                Assert.True(otherMethods[name].IsStatic);
                Assert.Equal(returnType, otherMethods[name].ReturnType);
                Assert.Equal(parameterCount, otherMethods[name].GetParameters().Length);
            }

            var unregisterParameters = otherMethods["Unregister"].GetParameters();
            AssertParameter(unregisterParameters[0], "key", typeof(ConsoleKey));
            AssertParameter(unregisterParameters[1], "modifiers", typeof(ConsoleModifiers), (ConsoleModifiers)0);
            AssertParameter(
                Assert.Single(otherMethods["RegisterScope"].GetParameters()),
                "owner",
                typeof(object));
            AssertParameter(
                Assert.Single(otherMethods["UnregisterByOwner"].GetParameters()),
                "owner",
                typeof(object));
            var formatKeyComboParameters = otherMethods["FormatKeyCombo"].GetParameters();
            Assert.Equal(2, formatKeyComboParameters.Length);
            AssertParameter(formatKeyComboParameters[0], "key", typeof(ConsoleKey));
            AssertParameter(formatKeyComboParameters[1], "modifiers", typeof(ConsoleModifiers));

            var entryType = typeof(HotkeyManager.HotkeyEntry);
            var contextType = typeof(HotkeyContext);
            foreach (var type in new[] { entryType, contextType })
            {
                Assert.True(type.IsClass);
                Assert.True(type.IsSealed);
                Assert.False(type.IsAbstract);
                Assert.Equal(typeof(object), type.BaseType);
                Assert.Empty(type.GetInterfaces());
                Assert.Empty(type.GetEvents(PublicDeclared));
                Assert.Empty(type.GetFields(PublicDeclared));
            }
            Assert.True(entryType.IsNestedPublic);
            Assert.True(contextType.IsPublic);
            Assert.DoesNotContain(entryType.GetMethods(PublicDeclared), method => !method.IsSpecialName);

            var entryConstructor = Assert.Single(entryType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            var entryConstructorParameters = entryConstructor.GetParameters();
            Assert.Equal(3, entryConstructorParameters.Length);
            AssertParameter(entryConstructorParameters[0], "description", typeof(string));
            AssertParameter(entryConstructorParameters[1], "handler", typeof(Func<Task>));
            AssertParameter(entryConstructorParameters[2], "owner", typeof(object), null);

            var entryProperties = entryType.GetProperties(PublicDeclared);
            Assert.Equal(
                new[] { "Description", "Handler", "Owner" },
                entryProperties.Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
            AssertReadOnlyProperty(entryProperties, "Description", typeof(string));
            AssertReadOnlyProperty(entryProperties, "Handler", typeof(Func<Task>));
            AssertReadOnlyProperty(entryProperties, "Owner", typeof(object));

            Assert.Empty(Assert.Single(
                contextType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)).GetParameters());

            var contextProperties = contextType.GetProperties(PublicDeclared);
            Assert.Equal(new[] { "LineCount" }, contextProperties.Select(property => property.Name).ToArray());
            AssertReadOnlyProperty(contextProperties, "LineCount", typeof(int));

            var contextMethods = contextType.GetMethods(PublicDeclared)
                .Where(method => !method.IsSpecialName)
                .ToDictionary(method => method.Name);
            Assert.Equal(
                new[] { "AddLine", "BindShortcut" },
                contextMethods.Keys.Order(StringComparer.Ordinal).ToArray());
            Assert.All(contextMethods.Values, method =>
            {
                Assert.False(method.IsStatic);
                Assert.Equal(typeof(HotkeyContext), method.ReturnType);
                Assert.Single(method.GetParameters());
            });
            AssertParameter(
                Assert.Single(contextMethods["AddLine"].GetParameters()),
                "text",
                typeof(string),
                string.Empty);
            AssertParameter(
                Assert.Single(contextMethods["BindShortcut"].GetParameters()),
                "shortcut",
                typeof(UiShortcut));
        }

        [Fact]
        public void TerminalUiPublicApiMatchesTargetManifest()
        {
            var type = typeof(TerminalUi);
            Assert.True(type.IsPublic);
            Assert.True(type.IsClass);
            Assert.True(type.IsAbstract);
            Assert.True(type.IsSealed);
            Assert.Equal(typeof(object), type.BaseType);
            Assert.Empty(type.GetInterfaces());
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.Empty(type.GetProperties(PublicDeclared));
            Assert.Empty(type.GetEvents(PublicDeclared));
            Assert.Empty(type.GetFields(PublicDeclared));

            var methods = type.GetMethods(PublicDeclared)
                .Where(method => !method.IsSpecialName)
                .ToDictionary(method => method.Name);
            Assert.Equal(
                new[]
                {
                    "Acknowledge",
                    "Ask",
                    "Confirm",
                    "Log",
                    "MultiSelect",
                    "Notify",
                    "Select"
                },
                methods.Keys.Order(StringComparer.Ordinal).ToArray());
            foreach (var (name, parameterCount) in new[]
            {
                ("Acknowledge", 2),
                ("Ask", 4),
                ("Confirm", 3),
                ("Log", 3),
                ("MultiSelect", 5),
                ("Notify", 4),
                ("Select", 4)
            })
            {
                Assert.True(methods[name].IsStatic);
                Assert.Equal(parameterCount, methods[name].GetParameters().Length);
            }

            var select = methods["Select"];
            Assert.True(select.IsGenericMethodDefinition);
            var selectType = Assert.Single(select.GetGenericArguments());
            Assert.Equal(GenericParameterAttributes.None, selectType.GenericParameterAttributes);
            Assert.Empty(selectType.GetGenericParameterConstraints());
            Assert.Equal(selectType, select.ReturnType);
            var selectParameters = select.GetParameters();
            Assert.Equal(4, selectParameters.Length);
            AssertParameter(selectParameters[0], "title", typeof(string));
            AssertParameter(selectParameters[1], "choices", typeof(IEnumerable<>).MakeGenericType(selectType));
            AssertParameter(selectParameters[2], "converter", typeof(Func<,>).MakeGenericType(selectType, typeof(string)), null);
            AssertParameter(selectParameters[3], "cancellationToken", typeof(CancellationToken), null);

            var multiSelect = methods["MultiSelect"];
            Assert.True(multiSelect.IsGenericMethodDefinition);
            var multiSelectType = Assert.Single(multiSelect.GetGenericArguments());
            Assert.Equal(GenericParameterAttributes.None, multiSelectType.GenericParameterAttributes);
            Assert.Empty(multiSelectType.GetGenericParameterConstraints());
            Assert.Equal(
                typeof(IReadOnlyList<>).MakeGenericType(multiSelectType),
                multiSelect.ReturnType);
            var multiSelectParameters = multiSelect.GetParameters();
            Assert.Equal(5, multiSelectParameters.Length);
            AssertParameter(multiSelectParameters[0], "title", typeof(string));
            AssertParameter(multiSelectParameters[1], "choices", typeof(IEnumerable<>).MakeGenericType(multiSelectType));
            AssertParameter(multiSelectParameters[2], "selected", typeof(IEnumerable<>).MakeGenericType(multiSelectType), null);
            AssertParameter(multiSelectParameters[3], "converter", typeof(Func<,>).MakeGenericType(multiSelectType, typeof(string)), null);
            AssertParameter(multiSelectParameters[4], "cancellationToken", typeof(CancellationToken), null);

            var ask = methods["Ask"];
            Assert.Equal(typeof(string), ask.ReturnType);
            var askParameters = ask.GetParameters();
            AssertParameter(askParameters[0], "title", typeof(string));
            AssertParameter(askParameters[1], "value", typeof(string), null);
            AssertParameter(askParameters[2], "allowEmpty", typeof(bool), false);
            AssertParameter(askParameters[3], "cancellationToken", typeof(CancellationToken), null);

            var confirm = methods["Confirm"];
            Assert.Equal(typeof(bool), confirm.ReturnType);
            var confirmParameters = confirm.GetParameters();
            AssertParameter(confirmParameters[0], "title", typeof(string));
            AssertParameter(confirmParameters[1], "defaultValue", typeof(bool), false);
            AssertParameter(confirmParameters[2], "cancellationToken", typeof(CancellationToken), null);

            var acknowledge = methods["Acknowledge"];
            Assert.Equal(typeof(bool), acknowledge.ReturnType);
            var acknowledgeParameters = acknowledge.GetParameters();
            AssertParameter(acknowledgeParameters[0], "title", typeof(string), "按 Enter 返回");
            AssertParameter(acknowledgeParameters[1], "cancellationToken", typeof(CancellationToken), null);

            var log = methods["Log"];
            Assert.Equal(typeof(void), log.ReturnType);
            var logParameters = log.GetParameters();
            AssertParameter(logParameters[0], "source", typeof(string));
            AssertParameter(logParameters[1], "text", typeof(string));
            AssertParameter(logParameters[2], "severity", typeof(UiSeverity), UiSeverity.Info);

            var notify = methods["Notify"];
            Assert.Equal(typeof(void), notify.ReturnType);
            var notifyParameters = notify.GetParameters();
            AssertParameter(notifyParameters[0], "source", typeof(string));
            AssertParameter(notifyParameters[1], "text", typeof(string));
            AssertParameter(notifyParameters[2], "severity", typeof(UiSeverity), UiSeverity.Info);
            AssertParameter(notifyParameters[3], "ttl", typeof(TimeSpan?), null);
        }

        [Fact]
        public void PluginContextMatchesDestructiveCutover()
        {
            var hostAssembly = typeof(IPluginContext).Assembly;
            Assert.Null(hostAssembly.GetType("UmamusumeResponseAnalyzer.TerminalGui.IWorkspaceOutput"));
            Assert.Null(hostAssembly.GetType("UmamusumeResponseAnalyzer.TerminalGui.PluginWorkspaceOutput"));
            Assert.DoesNotContain(
                hostAssembly.GetTypes().SelectMany(type => type.GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly)),
                method =>
                    method.GetCustomAttribute<ExtensionAttribute>() is not null &&
                    method.GetParameters().FirstOrDefault()?.ParameterType == typeof(Workspace));

            var contextType = typeof(IPluginContext);
            Assert.True(contextType.IsPublic);
            Assert.True(contextType.IsInterface);
            Assert.True(contextType.IsAbstract);
            Assert.Null(contextType.BaseType);
            Assert.Empty(contextType.GetInterfaces());
            Assert.Empty(contextType.GetConstructors());
            Assert.Empty(contextType.GetEvents(PublicDeclared));
            Assert.Empty(contextType.GetFields(PublicDeclared));
            Assert.DoesNotContain(contextType.GetMethods(PublicDeclared), method => !method.IsSpecialName);

            var contextProperties = contextType.GetProperties(PublicDeclared);
            Assert.Equal(
                new[] { "Analyzers", "Application", "Events" },
                contextProperties.Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
            AssertReadOnlyProperty(contextProperties, "Application", typeof(IApplication));
            AssertReadOnlyProperty(contextProperties, "Events", typeof(IPluginHostEvents));
            AssertReadOnlyProperty(contextProperties, "Analyzers", typeof(IPluginAnalyzerRegistry));

            var uiHostType = hostAssembly.GetType(
                "UmamusumeResponseAnalyzer.TerminalGui.UiHost",
                throwOnError: true)!;
            Assert.DoesNotContain(
                uiHostType.GetMethods(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly),
                method => method.Name == "ForPlugin");
        }

        [Fact]
        public void PluginLifecyclePublicApiMatchesTargetManifest()
        {
            var outcomeType = typeof(PluginManager.PluginLifecycleOutcome);
            Assert.True(outcomeType.IsNestedPublic);
            Assert.True(outcomeType.IsEnum);
            Assert.Equal(typeof(int), Enum.GetUnderlyingType(outcomeType));
            Assert.Equal(
                new[] { "Failed", "Succeeded", "RestartRequired" },
                Enum.GetNames<PluginManager.PluginLifecycleOutcome>());
            Assert.Equal(
                new[] { 0, 1, 2 },
                Enum.GetValues<PluginManager.PluginLifecycleOutcome>()
                    .Select(value => (int)value)
                    .ToArray());

            var resultType = typeof(PluginManager.PluginLifecycleResult);
            Assert.True(resultType.IsNestedPublic);
            Assert.True(resultType.IsClass);
            Assert.True(resultType.IsSealed);
            Assert.Equal(typeof(object), resultType.BaseType);
            Assert.Equal(
                new[] { typeof(IEquatable<PluginManager.PluginLifecycleResult>) },
                resultType.GetInterfaces());
            Assert.Empty(resultType.GetEvents(PublicDeclared));
            Assert.Empty(resultType.GetFields(PublicDeclared));
            var constructor = Assert.Single(resultType.GetConstructors(
                BindingFlags.Public | BindingFlags.Instance));
            var constructorParameters = constructor.GetParameters();
            Assert.Equal(2, constructorParameters.Length);
            AssertParameter(constructorParameters[0], "PluginName", typeof(string));
            AssertParameter(
                constructorParameters[1],
                "Outcome",
                typeof(PluginManager.PluginLifecycleOutcome));
            var properties = resultType.GetProperties(PublicDeclared);
            Assert.Equal(
                new[] { "Outcome", "PluginName" },
                properties.Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
            AssertReadWriteProperty(properties, "PluginName", typeof(string));
            AssertReadWriteProperty(
                properties,
                "Outcome",
                typeof(PluginManager.PluginLifecycleOutcome));

            var reload = Assert.Single(
                typeof(PluginManager).GetMethods(PublicDeclared),
                method => method.Name == nameof(PluginManager.ReloadPluginsAsync));
            Assert.True(reload.IsPublic);
            Assert.True(reload.IsStatic);
            Assert.False(reload.IsGenericMethod);
            Assert.Equal(
                typeof(Task<IReadOnlyList<PluginManager.PluginLifecycleResult>>),
                reload.ReturnType);
            AssertParamsParameter(
                Assert.Single(reload.GetParameters()),
                "pluginNames",
                typeof(string[]));
        }

        [Fact]
        public void TerminalUiLifecycleIsOneShot()
        {
            var hostAssembly = typeof(TerminalUi).Assembly;
            var uiHostType = hostAssembly.GetType(
                "UmamusumeResponseAnalyzer.TerminalGui.UiHost",
                throwOnError: true)!;
            var lifecycleMethods = typeof(TerminalUi).GetMethods(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly);
            Assert.DoesNotContain(
                lifecycleMethods,
                method =>
                    (method.IsPublic || method.IsAssembly) &&
                    (method.Name.Contains("Bind", StringComparison.OrdinalIgnoreCase) ||
                     method.Name.Contains("Reset", StringComparison.OrdinalIgnoreCase) ||
                     method.Name.Contains("Replace", StringComparison.OrdinalIgnoreCase)));

            var initialize = Assert.Single(lifecycleMethods, method => method.Name == "Initialize");
            Assert.True(initialize.IsAssembly);
            Assert.False(initialize.IsPublic);
            Assert.True(initialize.IsStatic);
            Assert.Equal(typeof(void), initialize.ReturnType);
            Assert.Equal(uiHostType, Assert.Single(initialize.GetParameters()).ParameterType);

            var requireHost = Assert.Single(lifecycleMethods, method => method.Name == "RequireHost");
            Assert.True(requireHost.IsAssembly);
            Assert.False(requireHost.IsPublic);
            Assert.True(requireHost.IsStatic);
            Assert.Equal(uiHostType, requireHost.ReturnType);
            Assert.Empty(requireHost.GetParameters());

            Assert.Equal(
                new[] { initialize },
                lifecycleMethods
                    .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == uiHostType))
                    .ToArray());
        }

        internal const string SyntheticFuturePluginSource = """
                using System;
                using System.Collections.Generic;
                using System.Threading;
                using System.Threading.Tasks;
                using Terminal.Gui.ViewBase;
                using UmamusumeResponseAnalyzer.Plugin;
                using UmamusumeResponseAnalyzer.TerminalGui;

                public sealed record SyntheticExerciseResult(
                    Workspace Workspace, string WorkspaceTitle,
                    string PanelKey, string PanelTitle, string PanelText,
                    string LogText, string NotificationText,
                    bool CanonicalReference, bool CurrentReference,
                    bool SharedPanelRemoved, bool MissingPanelRemoved,
                    bool BackgroundPanelRemoved, bool RecreatedGeneration,
                    bool RecreatedCanonicalReference, bool RecreatedCurrentReference,
                    int TombstoneFailureCount)
                {
                    public string Format() => string.Join(
                        Environment.NewLine,
                        new[]
                        {
                            $"WorkspaceTitlePrefix={WorkspaceTitle.StartsWith("Synthetic Future Workspace ", StringComparison.Ordinal)}",
                            $"CanonicalReference={CanonicalReference}",
                            $"CurrentReference={CurrentReference}",
                            $"SharedPanelRemoved={SharedPanelRemoved}",
                            $"MissingPanelRemoved={MissingPanelRemoved}",
                            $"BackgroundPanelRemoved={BackgroundPanelRemoved}",
                            $"RecreatedGeneration={RecreatedGeneration}",
                            $"RecreatedCanonicalReference={RecreatedCanonicalReference}",
                            $"RecreatedCurrentReference={RecreatedCurrentReference}",
                            $"TombstoneFailureCount={TombstoneFailureCount}"
                        });
                }

                public sealed class SyntheticFuturePlugin : IPlugin
                {
                    public string Name => "Synthetic Future Plugin";
                    public string Author => "ABI Tests";
                    public string[] Targets => Array.Empty<string>();

                    public void Initialize(IPluginContext context)
                    {
                        _ = context.Application;
                        _ = context.Events;
                        _ = context.Analyzers;
                    }

                    public static async Task<SyntheticExerciseResult> ExerciseAsync()
                    {
                        const string panelKey = "shared";
                        const string finalPanelTitle = "Second caller final";
                        const string finalPanelText = "second caller final content";
                        const string sharedLogText = "synthetic shared log";
                        const string sharedNotificationText = "synthetic shared notification";
                        var workspaceTitle = $"Synthetic Future Workspace {Guid.NewGuid():N}";
                        var workspace = Workspace.Create(workspaceTitle);
                        var canonical = Workspace.Create(workspaceTitle.ToUpperInvariant());
                        var canonicalReference = ReferenceEquals(workspace, canonical);

                        workspace.SwitchTo();
                        var currentReference = ReferenceEquals(Workspace.Current, workspace);

                        SyntheticPanelWriterA.Write(workspace, panelKey, "First caller", "first caller content");
                        SyntheticPanelWriterB.Write(canonical, panelKey, "Second caller", "second caller content");
                        var removal = SyntheticPanelRemover.Remove(canonical, panelKey);

                        var shortcut = new UiShortcut(ConsoleKey.F19, () => Task.CompletedTask, ConsoleModifiers.Alt);
                        var (shortcutKey, shortcutHandler, shortcutModifiers) = shortcut;
                        Require(shortcutKey == shortcut.Key, "UiShortcut deconstruction must preserve Key.");
                        Require(ReferenceEquals(shortcutHandler, shortcut.Handler), "UiShortcut deconstruction must preserve Handler.");
                        Require(shortcutModifiers == shortcut.Modifiers, "UiShortcut deconstruction must preserve Modifiers.");
                        var equivalentShortcut = new UiShortcut(shortcut.Key, shortcut.Handler, shortcut.Modifiers);
                        Require(shortcut == equivalentShortcut && shortcut.Equals(equivalentShortcut), "UiShortcut record equality must use positional values.");
                        var modifiedShortcut = shortcut with { Modifiers = ConsoleModifiers.Shift };
                        Require(modifiedShortcut.Modifiers == ConsoleModifiers.Shift, "UiShortcut with expression must update positional values.");

                        var directContent = new WorkspaceContent(() => WorkspaceContent.Text("direct").CreateView());
                        Func<View> createView = directContent.CreateView;
                        _ = createView;

                        TerminalUi.Log("Synthetic", "live", UiSeverity.Success);
                        workspace.Notify("live", UiSeverity.Info, TimeSpan.Zero, shortcut);
                        workspace.BindHotkey(ConsoleKey.F20, description: "Synthetic workspace");

                        var backgroundPanelRemoved = await Task.Run(() =>
                        {
                            workspace.SetPanel("background", "Background", WorkspaceContent.Text("background"), fullBleed: true, switchToWorkspace: false);
                            var removed = workspace.RemovePanel("background");
                            TerminalUi.Log("Synthetic", "background", UiSeverity.Trace);
                            workspace.Notify("background", UiSeverity.Warning, TimeSpan.Zero, Array.Empty<UiShortcut>());
                            workspace.SwitchTo();
                            workspace.BindHotkey(ConsoleKey.F21, ConsoleModifiers.Shift, "Synthetic background workspace");
                            return removed;
                        });

                        workspace.Remove();
                        workspace.Remove();

                        var recreated = Workspace.Create(workspaceTitle.ToLowerInvariant());
                        var recreatedGeneration = !ReferenceEquals(workspace, recreated);
                        var recreatedCanonicalReference = ReferenceEquals(recreated, Workspace.Create(workspaceTitle.ToUpperInvariant()));
                        recreated.SwitchTo();
                        var recreatedCurrentReference = ReferenceEquals(Workspace.Current, recreated);
                        var tombstoneFailureCount = VerifyTombstone(workspace);

                        ExerciseHotkeySurface(shortcut);
                        ExerciseDialogSurface();

                        SyntheticPanelWriterA.Write(recreated, panelKey, "First caller final", "first caller final content");
                        SyntheticPanelWriterB.Write(recreated, panelKey, finalPanelTitle, finalPanelText);
                        TerminalUi.Log("Synthetic", sharedLogText, UiSeverity.Success);
                        recreated.Notify(sharedNotificationText, UiSeverity.Info, TimeSpan.FromMinutes(1), shortcut);

                        return new(
                            recreated, workspaceTitle,
                            panelKey, finalPanelTitle, finalPanelText,
                            sharedLogText, sharedNotificationText,
                            canonicalReference, currentReference,
                            removal.Existing, removal.Missing,
                            backgroundPanelRemoved, recreatedGeneration,
                            recreatedCanonicalReference, recreatedCurrentReference,
                            tombstoneFailureCount);
                    }

                    public static void Cleanup(SyntheticExerciseResult result) => result.Workspace.Remove();

                    public static void UnregisterAllInIsolatedChildProcess() => HotkeyManager.UnregisterAll();

                    static void ExerciseDialogSurface()
                    {
                        var cancellation = new CancellationToken(canceled: true);
                        var choices = new[] { "one" };
                        ExpectCanceled(() => TerminalUi.Select("Select value", new[] { 1 }, cancellationToken: cancellation));
                        ExpectCanceled(() => TerminalUi.MultiSelect("MultiSelect", choices, cancellationToken: cancellation));
                        ExpectCanceled(() => TerminalUi.Ask("Ask", cancellationToken: cancellation));
                        ExpectCanceled(() => TerminalUi.Confirm("Confirm", cancellationToken: cancellation));
                        ExpectCanceled(() => TerminalUi.Acknowledge(cancellationToken: cancellation));
                    }

                    static void ExpectCanceled(Action action)
                    {
                        try
                        {
                            action();
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        throw new InvalidOperationException("TerminalUi dialog must observe deterministic cancellation.");
                    }

                    static void ExerciseHotkeySurface(UiShortcut shortcut)
                    {
                        var owner = new object();
                        var previousDelay = HotkeyManager.PopupAutoCloseDelay;
                        try
                        {
                            Require(
                                !HotkeyManager.Hotkeys.ContainsKey((ConsoleKey.F13, ConsoleModifiers.Alt)) &&
                                !HotkeyManager.Hotkeys.ContainsKey((ConsoleKey.F14, ConsoleModifiers.Alt)) &&
                                !HotkeyManager.Hotkeys.ContainsKey((ConsoleKey.F15, (ConsoleModifiers)0)) &&
                                !HotkeyManager.Hotkeys.ContainsKey((ConsoleKey.F16, (ConsoleModifiers)0)),
                                "Synthetic hotkey keys must be unused.");

                            HotkeyManager.PopupAutoCloseDelay = TimeSpan.FromMilliseconds(10);
                            Func<Task> handler = () => Task.CompletedTask;
                            Func<HotkeyContext, Task> contextHandler = context =>
                            {
                                context.AddLine("handled").BindShortcut(shortcut);
                                return Task.CompletedTask;
                            };

                            using (HotkeyManager.RegisterScope(owner))
                            {
                                HotkeyManager.Register(ConsoleKey.F13, ConsoleModifiers.Alt, "with modifiers", handler);
                                HotkeyManager.Register(ConsoleKey.F14, ConsoleModifiers.Alt, "context with modifiers", contextHandler);
                                HotkeyManager.Register(ConsoleKey.F15, "without modifiers", handler);
                                HotkeyManager.Register(ConsoleKey.F16, "context without modifiers", contextHandler);
                            }

                            var namedTupleObserved = false;
                            foreach (var combo in HotkeyManager.Hotkeys.Keys)
                            {
                                if (combo.Key == ConsoleKey.F13 && combo.Modifiers == ConsoleModifiers.Alt)
                                {
                                    namedTupleObserved = true;
                                    break;
                                }
                            }
                            Require(namedTupleObserved, "Hotkeys keys must expose Key and Modifiers tuple names.");
                            Require(HotkeyManager.FormatKeyCombo(ConsoleKey.F14, ConsoleModifiers.Alt).Length > 0, "FormatKeyCombo must return display text.");

                            var entry = new HotkeyManager.HotkeyEntry("entry", handler, owner);
                            Require(entry.Description == "entry", "HotkeyEntry Description must round-trip.");
                            Require(ReferenceEquals(entry.Handler, handler), "HotkeyEntry Handler must round-trip.");
                            Require(ReferenceEquals(entry.Owner, owner), "HotkeyEntry Owner must round-trip.");

                            var context = new HotkeyContext();
                            context.AddLine().BindShortcut(shortcut);
                            Require(context.LineCount == 1, "HotkeyContext must expose its line count.");
                            Require(HotkeyManager.Unregister(ConsoleKey.F13, ConsoleModifiers.Alt), "Unregister with modifiers must remove its registration.");
                            Require(HotkeyManager.Unregister(ConsoleKey.F15), "Unregister with default modifiers must remove its registration.");
                            Require(HotkeyManager.UnregisterByOwner(owner) == 2, "UnregisterByOwner must remove the remaining scoped registrations.");
                        }
                        finally
                        {
                            HotkeyManager.UnregisterByOwner(owner);
                            HotkeyManager.PopupAutoCloseDelay = previousDelay;
                        }
                    }

                    static int VerifyTombstone(Workspace removed)
                    {
                        var calls = new (string Name, Action Invoke)[]
                        {
                            ("RemovePanel", () => { removed.RemovePanel("missing"); }),
                            ("SetPanel", () => removed.SetPanel("removed", "Removed", WorkspaceContent.Text("removed"))),
                            ("Notify", () => removed.Notify("removed")),
                            ("SwitchTo", removed.SwitchTo),
                            ("BindHotkey", () => removed.BindHotkey(ConsoleKey.F22))
                        };
                        foreach (var call in calls)
                            ExpectRemoved(call.Name, call.Invoke, removed.Title);
                        return calls.Length;
                    }

                    static void ExpectRemoved(string operation, Action action, string title)
                    {
                        try
                        {
                            action();
                        }
                        catch (InvalidOperationException exception)
                            when (exception.Message.Contains(title, StringComparison.OrdinalIgnoreCase) &&
                                  exception.Message.Contains("removed", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        throw new InvalidOperationException($"{operation} on removed workspace '{title}' did not fail with the required tombstone message.");
                    }

                    static void Require(bool condition, string message)
                    {
                        if (!condition)
                            throw new InvalidOperationException(message);
                    }
                }

                static class SyntheticPanelWriterA
                {
                    public static void Write(Workspace workspace, string key, string title, string text)
                        => workspace.SetPanel(key, title, WorkspaceContent.Text(text), switchToWorkspace: false);
                }

                static class SyntheticPanelWriterB
                {
                    public static void Write(Workspace workspace, string key, string title, string text)
                        => workspace.SetPanel(key, title, WorkspaceContent.Text(text), fullBleed: true, switchToWorkspace: false);
                }

                static class SyntheticPanelRemover
                {
                    public static (bool Existing, bool Missing) Remove(Workspace workspace, string key)
                        => (workspace.RemovePanel(key), workspace.RemovePanel(key));
                }
                """;

        [Fact]
        public async Task SyntheticFuturePluginLoadsAndExercisesTargetAbi()
        {
            const string scenario = "synthetic-abi";
            if (TerminalUiLifecycleChildProcess.IsChild(scenario))
            {
                TerminalUiLifecycleChildProcess.WriteResult(RunSyntheticFuturePlugin());
                return;
            }

            var formatted = await TerminalUiLifecycleProcessTests.RunChildAsync(
                scenario,
                typeof(PluginAbiBoundaryTests),
                nameof(SyntheticFuturePluginLoadsAndExercisesTargetAbi));
            Assert.Equal(
                string.Join(
                    Environment.NewLine,
                    [
                        "WorkspaceTitlePrefix=True",
                        "CanonicalReference=True",
                        "CurrentReference=True",
                        "SharedPanelRemoved=True",
                        "MissingPanelRemoved=False",
                        "BackgroundPanelRemoved=True",
                        "RecreatedGeneration=True",
                        "RecreatedCanonicalReference=True",
                        "RecreatedCurrentReference=True",
                        "TombstoneFailureCount=5",
                        "RuntimePanelObserved=True",
                        "RuntimeLogObserved=True",
                        "RuntimeNotificationObserved=True"
                    ]),
                formatted);
        }

        internal static string RunSyntheticFuturePlugin()
        {
            var dllPath = Path.Combine(
                Path.GetTempPath(),
                $"ura-synthetic-future-plugin-{Guid.NewGuid():N}.dll");
            using var terminal = new TerminalGuiTestApp();
            var host = TerminalUiLifecycleChildProcess.InitializeHost(
                terminal,
                CancellationToken.None);
            var run = terminal.StartAsync(host).GetAwaiter().GetResult();
            var baseline = Workspace.Create("M6 synthetic baseline");
            baseline.SwitchTo();
            host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var originalCurrent = Assert.IsType<Workspace>(Workspace.Current);
            Assert.Same(baseline, originalCurrent);

            Assembly? assembly = null;
            object? result = null;
            Workspace? exercisedWorkspace = null;
            string? formatted = null;
            try
            {
                PluginCompiler.Compile(
                    SyntheticFuturePluginSource,
                    "SyntheticFuturePlugin",
                    dllPath);
                assembly = Assembly.Load(File.ReadAllBytes(dllPath));
                var pluginType = assembly.GetType("SyntheticFuturePlugin", throwOnError: true)!;
                var exercise = pluginType.GetMethod(
                    "ExerciseAsync",
                    BindingFlags.Public | BindingFlags.Static)!;
                var task = Assert.IsAssignableFrom<Task>(exercise.Invoke(null, null));

                task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                result = exercise.ReturnType.GetProperty("Result")!.GetValue(task);
                Assert.NotNull(result);
                host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

                exercisedWorkspace = Assert.IsType<Workspace>(
                    result.GetType().GetProperty("Workspace")!.GetValue(result));
                formatted = Assert.IsType<string>(
                    result.GetType().GetMethod("Format", PublicDeclared)!.Invoke(result, null));
                var panelText = Assert.IsType<string>(
                    result.GetType().GetProperty("PanelText")!.GetValue(result));
                var logText = Assert.IsType<string>(
                    result.GetType().GetProperty("LogText")!.GetValue(result));
                var notificationText = Assert.IsType<string>(
                    result.GetType().GetProperty("NotificationText")!.GetValue(result));

                var logs = terminal.InvokeAsync(host.GetLogsForTests)
                    .GetAwaiter().GetResult();
                var logObserved = logs.Any(line =>
                    line.Text == $"[Synthetic] {logText}" &&
                    line.Severity == UiSeverity.Success);
                var notifications = terminal.InvokeAsync(() =>
                        host.GetNotificationsForTests(exercisedWorkspace))
                    .GetAwaiter().GetResult();
                var notificationObserved = notifications.Any(notification =>
                    ReferenceEquals(notification.Workspace, exercisedWorkspace) &&
                    notification.Text == notificationText &&
                    notification.Severity == UiSeverity.Info);

                exercisedWorkspace.SwitchTo();
                host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                terminal.RedrawAsync().GetAwaiter().GetResult();
                var screen = terminal.CaptureScreenAsync().GetAwaiter().GetResult();
                var panelObserved =
                    screen.Contains(panelText, StringComparison.Ordinal) &&
                    !screen.Contains("first caller final content", StringComparison.Ordinal);

                Assert.True(panelObserved);
                Assert.True(logObserved);
                Assert.True(notificationObserved);
                formatted = string.Join(
                    Environment.NewLine,
                    [
                        formatted,
                        $"RuntimePanelObserved={panelObserved}",
                        $"RuntimeLogObserved={logObserved}",
                        $"RuntimeNotificationObserved={notificationObserved}"
                    ]);
            }
            finally
            {
                try
                {
                    if (assembly is not null && result is not null)
                    {
                        assembly.GetType("SyntheticFuturePlugin", throwOnError: true)!
                            .GetMethod("Cleanup", BindingFlags.Public | BindingFlags.Static)!
                            .Invoke(null, [result]);
                        if (!ReferenceEquals(originalCurrent, exercisedWorkspace))
                        {
                            originalCurrent.SwitchTo();
                        }
                        host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                        Assert.Same(originalCurrent, Workspace.Current);
                    }
                }
                finally
                {
                    try
                    {
                        File.Delete(dllPath);
                    }
                    finally
                    {
                        terminal.StopAsync(host, run).GetAwaiter().GetResult();
                    }
                }
            }

            Assert.NotNull(formatted);
            return formatted;
        }

        [Fact]
        public void GeneratedGallopSourcesAreSourceOnlyAtOutputRoot()
        {
            var repositoryRoot = FindRepositoryRoot();
            var gallopRoot = Path.Combine(repositoryRoot.FullName, "UmamusumeResponseAnalyzer", "Gallop");

            Assert.True(Directory.Exists(gallopRoot), $"找不到 Gallop generated source directory: {gallopRoot}");
            Assert.False(File.Exists(Path.Combine(gallopRoot, "_generated.txt")), "Gallop generated sources should not include _generated.txt.");
            Assert.False(Directory.Exists(Path.Combine(gallopRoot, "Gallop")), "Gallop generated sources should be directly under the Gallop output directory.");
            Assert.True(File.Exists(Path.Combine(gallopRoot, "RequestBase.cs")), "Gallop DTO sources should be directly under the Gallop output directory.");
            Assert.True(File.Exists(Path.Combine(gallopRoot, "Endpoints", "GameApi.g.cs")), "Gallop endpoint sources should be directly under Gallop/Endpoints.");
        }

        static void AssertParameter(
            ParameterInfo parameter,
            string name,
            Type type)
            => AssertParameter(parameter, name, type, hasDefault: false, defaultValue: null, isParams: false);

        static void AssertParameter(
            ParameterInfo parameter,
            string name,
            Type type,
            object? defaultValue)
            => AssertParameter(parameter, name, type, hasDefault: true, defaultValue, isParams: false);

        static void AssertParamsParameter(
            ParameterInfo parameter,
            string name,
            Type type)
            => AssertParameter(parameter, name, type, hasDefault: false, defaultValue: null, isParams: true);

        static void AssertParameter(
            ParameterInfo parameter,
            string name,
            Type type,
            bool hasDefault,
            object? defaultValue,
            bool isParams)
        {
            Assert.Equal(name, parameter.Name);
            Assert.Equal(type, parameter.ParameterType);
            Assert.Equal(hasDefault, parameter.HasDefaultValue);
            if (hasDefault)
            {
                if (defaultValue?.GetType().IsEnum == true)
                    Assert.Equal(Convert.ToInt64(defaultValue), Convert.ToInt64(parameter.DefaultValue));
                else
                    Assert.Equal(defaultValue, parameter.DefaultValue);
            }
            Assert.Equal(isParams, parameter.GetCustomAttribute<ParamArrayAttribute>() is not null);
        }

        static void AssertReadOnlyProperty(
            IEnumerable<PropertyInfo> properties,
            string name,
            Type type)
        {
            var property = Assert.Single(properties, property => property.Name == name);
            Assert.Equal(type, property.PropertyType);
            Assert.NotNull(property.GetMethod);
            Assert.False(property.GetMethod.IsStatic);
            Assert.Null(property.SetMethod);
        }

        static void AssertReadWriteProperty(
            IEnumerable<PropertyInfo> properties,
            string name,
            Type type)
        {
            var property = Assert.Single(properties, property => property.Name == name);
            Assert.Equal(type, property.PropertyType);
            Assert.NotNull(property.GetMethod);
            Assert.False(property.GetMethod.IsStatic);
            Assert.NotNull(property.SetMethod);
            Assert.False(property.SetMethod.IsStatic);
        }

        static DirectoryInfo FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
        {
            var repositoryRoot = new FileInfo(sourceFilePath).Directory?.Parent;
            if (repositoryRoot is not null &&
                File.Exists(Path.Combine(repositoryRoot.FullName, "UmamusumeResponseAnalyzer.sln")))
                return repositoryRoot;

            throw new InvalidOperationException(
                $"编译期 source anchor 不在 UmamusumeResponseAnalyzer repository 中：{sourceFilePath}");
        }
    }
}
