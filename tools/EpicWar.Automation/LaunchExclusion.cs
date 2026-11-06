namespace EpicWar.Automation;

internal sealed class LaunchExclusion : IDisposable
{
    private readonly Mutex mutex;
    private LaunchExclusion(Mutex mutex){this.mutex=mutex;}
    internal static LaunchExclusion Acquire()
    {
        var mutex=new Mutex(false,"Local\\EpicWar.Automation.RA3.Session");
        bool acquired;
        try{acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
        if(!acquired){mutex.Dispose();throw new InvalidOperationException("Another Epic War launcher session is active.");}
        return new LaunchExclusion(mutex);
    }
    public void Dispose(){mutex.ReleaseMutex();mutex.Dispose();}
}
