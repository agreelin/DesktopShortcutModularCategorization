using System.Reflection;
using FolderSessionLock.App.ViewModels;
using FolderSessionLock.Broker.Lifecycle;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;

namespace FolderSessionLock.App.Tests;

public sealed class LifecycleBoundaryTests
{
    [Fact]
    public void MainViewModel_DoesNotOwnSchedulerOrFolderLockService()
    {
        FieldInfo[] fields = typeof(MainViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(fields, field => typeof(ILockTaskScheduler).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(fields, field => typeof(IFolderLockService).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(BrokerLifecycleController));
    }

    [Fact]
    public void MainViewModel_ExposesNoRemovalIntentEntryPoint()
    {
        MethodInfo[] methods = typeof(MainViewModel).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(methods, method =>
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(LockRemovalIntent)));
    }

    [Fact]
    public void MainWindow_DoesNotOwnSchedulerOrFolderLockService()
    {
        FieldInfo[] fields = typeof(MainWindow).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(fields, field => typeof(ILockTaskScheduler).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(fields, field => typeof(IFolderLockService).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(BrokerLifecycleController));
    }

    [Fact]
    public void App_DoesNotOwnSchedulerOrFolderLockService()
    {
        FieldInfo[] fields = typeof(App).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(fields, field => typeof(ILockTaskScheduler).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(fields, field => typeof(IFolderLockService).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(BrokerLifecycleController));
    }
}
