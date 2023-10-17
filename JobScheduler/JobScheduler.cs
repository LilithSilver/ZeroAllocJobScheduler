using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace JobScheduler;

/// <summary>
///     A <see cref="JobScheduler"/> schedules and processes <see cref="IJob"/>s asynchronously. Better-suited for larger jobs due to its underlying events. 
/// </summary>
public partial class JobScheduler : IDisposable
{
    /// <summary>
    /// Contains configuration settings for <see cref="JobScheduler"/>.
    /// </summary>
    public struct Config
    {
        /// <summary>
        /// Create a new <see cref="Config"/> for a <see cref="JobScheduler"/> with all default settings.
        /// </summary>
        public Config() { }

        /// <summary>
        /// Defines the maximum expected number of concurrent jobs. Increasing this number will allow more jobs to be scheduled
        /// without spontaneous allocation, but will increase total memory consumption and decrease performance.
        /// If unset, the default is <c>32</c>
        /// </summary>
        public int MaxExpectedConcurrentJobs { get; set; } = 32;

        /// <summary>
        /// Whether to use Strict Allocation Mode for this <see cref="JobScheduler"/>. If an allocation might occur, the JobScheduler
        /// will throw a <see cref="MaximumConcurrentJobCountExceededException"/>.
        /// Not recommended for production environments (spontaneous allocation is probably usually better than crashing the program).
        /// </summary>
        public bool StrictAllocationMode { get; set; } = false;

        /// <summary>
        /// The process name to use for spawned child threads. By default, set to the current domain's <see cref="AppDomain.FriendlyName"/>.
        /// Thread will be named "prefix0" for the first thread, "prefix1" for the second thread, etc.
        /// </summary>
        public string ThreadPrefixName { get; set; } = AppDomain.CurrentDomain.FriendlyName;

        /// <summary>
        /// The amount of worker threads to use. By default, set to <see cref="Environment.ProcessorCount"/>, the amount of hardware processors 
        /// available on the system.
        /// </summary>
        public int ThreadCount { get; set; } = Environment.ProcessorCount;
    }

    /// <summary>
    /// Thrown when <see cref="Config.StrictAllocationMode"/> is enabled and the <see cref="JobScheduler"/> goes over its <see cref="Config.MaxExpectedConcurrentJobs"/>.
    /// </summary>
    public class MaximumConcurrentJobCountExceededException : Exception
    {
        internal MaximumConcurrentJobCountExceededException() : base($"{nameof(JobScheduler)} has gone over its {nameof(Config.MaxExpectedConcurrentJobs)} value! " +
            $"Increase that value or disable {nameof(Config.StrictAllocationMode)} to allow spontaneous allocations.")
        {
        }
    }

    /// <summary>
    ///     Pairs a <see cref="JobID"/> with its <see cref="IJob"/> and other important meta-data. 
    /// </summary>
    private readonly struct JobMeta
    {
        public JobMeta(in JobId jobId, IJob? job)
        {
            JobID = jobId;
            Job = job;
        }

        /// <summary>
        ///     The <see cref="IJob"/>.
        /// </summary>
        public IJob? Job { get; }

        /// <summary>
        ///     The <see cref="JobId"/>.
        /// </summary>
        public JobId JobID { get; }
    }

    // Tracks how many threads are alive; interlocked
    private int _threadsAlive = 0;

    // internally visible for testing
    internal int ThreadsAlive => _threadsAlive;

    private readonly bool _strictAllocationMode;
    private readonly int _maxConcurrentJobs;

    // temporary list for passing deps into JobPool
    private readonly List<JobId> _dependencyCache;

    /// <summary>
    /// Creates an instance of the <see cref="JobScheduler"/>
    /// </summary>
    /// <param name="settings">The <see cref="Config"/> to use for this instance of <see cref="JobScheduler"/></param>
    public JobScheduler(in Config settings)
    {
        MainThreadID = Thread.CurrentThread.ManagedThreadId;

        var threads = settings.ThreadCount;
        if (threads <= 0)
        {
            threads = Environment.ProcessorCount;
        }

        _strictAllocationMode = settings.StrictAllocationMode;
        _maxConcurrentJobs = settings.MaxExpectedConcurrentJobs;
        _dependencyCache = new(settings.MaxExpectedConcurrentJobs - 1);

        // pre-fill all of our data structures up to the concurrent job max
        QueuedJobs = new(settings.MaxExpectedConcurrentJobs);
        JobPool = new(settings.MaxExpectedConcurrentJobs);

        // ConcurrentQueue doesn't have a segment size constructor so we have to use a hack with the IEnumerable constructor.
        // First, we add a bunch of dummy jobs to the old queue...
        for (var i = 0; i < settings.MaxExpectedConcurrentJobs; i++)
        {
            QueuedJobs.Add(default);
        }
        
        // ... then, we initialize the ConcurrentQueue with that collection. The segment size will be set to the count.
        // Note that this line WILL produce garbage due to IEnumerable iteration! (always boxes multiple enumerator structs)
        MasterQueue = new(QueuedJobs);
        
        // Then, we dequeue everything from the ConcurrentQueue. We can't Clear() because that'll nuke the segment.
        while (!MasterQueue.IsEmpty)
        {
            MasterQueue.TryDequeue(out var _);
        }
        
        // And then normally clear the normal queue we used.
        QueuedJobs.Clear();

        InitAlgorithm(threads, _maxConcurrentJobs, CancellationTokenSource.Token);

        // we count us as a thread
        _threadsAlive = threads;

        // spawn all the child threads
        for (var i = 0; i < threads; i++)
        {
            int c = i;
            var thread = new Thread(WorkerLoop)
            {
                Name = $"{settings.ThreadPrefixName}{i}"
            };
            thread.Start(i);
        }
    }

    /// <summary>
    /// Tracks which thread the JobScheduler was constructed on
    /// </summary>
    private int MainThreadID { get; }

    /// <summary>
    /// Tracks the overall state of all threads; when canceled in Dispose, all child threads are exited
    /// </summary>
    private CancellationTokenSource CancellationTokenSource { get; } = new();

    /// <summary>
    /// Jobs scheduled by the scheduler (NOT other jobs), but not yet flushed to the threads
    /// </summary>
    private List<JobMeta> QueuedJobs { get; }

    /// <summary>
    /// Jobs flushed and waiting to be picked up by worker threads
    /// </summary>
    private ConcurrentQueue<JobMeta> MasterQueue { get; }

    /// <summary>
    /// Tracks each job from scheduling to completion; when they complete, however, their data is removed from the pool and recycled.
    /// Note that we have to lock this, and can't use a ReaderWriterLock/ReaderWriterLockSlim because those allocate.
    /// </summary>
    private JobPool JobPool { get; }

    /// <summary>
    /// Returns whether this is the main thread the scheduler was created on
    /// </summary>
    private bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadID;

    /// <summary>
    ///     Schedules a <see cref="IJob"/> and returns its <see cref="JobHandle"/>.
    /// </summary>
    /// <param name="job">The <see cref="IJob"/>.</param>
    /// <param name="dependency">The <see cref="JobHandle"/>-Dependency.</param>
    /// <param name="dependencies">A list of additional <see cref="JobHandle"/>-Dependencies.</param>
    /// <returns>A <see cref="JobHandle"/>.</returns>
    /// <exception cref="InvalidOperationException">If called on a different thread than the <see cref="JobScheduler"/> was constructed on</exception>
    /// <exception cref="MaximumConcurrentJobCountExceededException">If the maximum amount of concurrent jobs is at maximum, and strict mode is enabled.</exception>
    private JobHandle Schedule(IJob? job, JobHandle? dependency = null, JobHandle[]? dependencies = null)
    {
        if (!IsMainThread)
        {
            throw new InvalidOperationException($"Can only call {nameof(Schedule)} from the thread that spawned the {nameof(JobScheduler)}!");
        }

        _dependencyCache.Clear();

        if (dependencies != null)
        {
            foreach (var d in dependencies)
            {
                _dependencyCache.Add(d.JobId);
            }
        }
        if (dependency != null) _dependencyCache.Add(dependency.Value.JobId);

        JobId jobId;
        bool ready;
        lock (JobPool)
        {
            if (_strictAllocationMode && JobPool.JobCount >= _maxConcurrentJobs)
            {
                throw new MaximumConcurrentJobCountExceededException();
            }
            jobId = JobPool.Schedule(_dependencyCache, job, out ready);
        }

        var handle = new JobHandle(this, jobId);
        var jobMeta = new JobMeta(jobId, job);

        // if we're ready, we can go ahead and prep the job. If not, we leave that up to the dependencies.
        if (ready)
        {
            // we only lock in debug mode for strict flushed-jobs checking within Complete()
#if DEBUG
            lock (QueuedJobs)
#endif
            {
                QueuedJobs.Add(jobMeta);
            }
        }
        return handle;
    }

    /// <summary>
    /// Schedules a job. It is only queued up, and will only begin processing when the user calls <see cref="Flush()"/> or when any in-progress dependencies complete.
    /// </summary>
    /// <param name="job">The job to process</param>
    /// <param name="dependency">A job that must complete before this job can be run</param>
    /// <returns>Its <see cref="JobHandle"/>.</returns>
    /// <exception cref="InvalidOperationException">If called on a different thread than the <see cref="JobScheduler"/> was constructed on</exception>
    /// <exception cref="MaximumConcurrentJobCountExceededException">If the maximum amount of concurrent jobs is at maximum, and strict mode is enabled.</exception>
    public JobHandle Schedule(IJob job, JobHandle? dependency = null)
    {
        if (dependency is not null)
        {
            CheckForSchedulerEquality(dependency.Value);
        }
        return Schedule(job, dependency, null);
    }


    /// <summary>
    ///     Combine multiple dependencies into a single <see cref="JobHandle"/> which is scheduled.
    /// </summary>
    /// <param name="dependencies">A list of handles to depend on. Assumed to not contain duplicates.</param>
    /// <returns>The combined <see cref="JobHandle"/></returns>
    /// <exception cref="InvalidOperationException">If called on a different thread than the <see cref="JobScheduler"/> was constructed on</exception>
    /// <exception cref="MaximumConcurrentJobCountExceededException">If the maximum amount of concurrent jobs is at maximum, and strict mode is enabled.</exception>
    public JobHandle CombineDependencies(JobHandle[] dependencies)
    {
        foreach (var dependency in dependencies)
        {
            CheckForSchedulerEquality(dependency);
        }
        return Schedule(null, null, dependencies);
    }

    /// <summary>
    ///     Checks if the passed <see cref="JobHandle"/> equals this <see cref="JobScheduler"/>.
    /// </summary>
    /// <param name="dependency">The <see cref="JobHandle"/>.</param>
    /// <exception cref="InvalidOperationException">Is thrown when the passed handle has a different scheduler.</exception>
    private void CheckForSchedulerEquality(JobHandle dependency)
    {
        if (!ReferenceEquals(dependency.Scheduler, this))
        {
            throw new InvalidOperationException($"Job dependency was scheduled with a different {nameof(JobScheduler)}!");
        }
    }

    /// <summary>
    /// Flushes all queued <see cref="IJob"/>'s to the worker threads. 
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush()
    {
        if (!IsMainThread)
        {
            throw new InvalidOperationException($"Can only call {nameof(Flush)} from the thread that spawned the {nameof(JobScheduler)}!");
        }

        if (QueuedJobs.Count == 0) return;

        // we only lock in debug mode for strict flushed-jobs checking within Complete()
#if DEBUG
        lock (QueuedJobs)
#endif
        {
            // QueuedJobs is guaranteed to only contain jobs that are ready
            foreach (var job in QueuedJobs)
            {
                // because this is a concurrentqueue (linkedlist implementation under the hood)
                // we don't have to worry about ordering issues (if someone dequeues while we do this, it'll just take the first one we added, which is fine)
                MasterQueue.Enqueue(job);
            }

            // clear the the incoming queue
            QueuedJobs.Clear();
        }

        // algorithm 1
        _notifier.NotifyOne();
    }

    /// <summary>
    /// Blocks the thread until the given job ID has been completed. Can be called from Jobs.
    /// </summary>
    /// <param name="jobID"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Complete(JobId jobID)
    {
        CheckIfJobIsFlushed(jobID);
        ManualResetEvent handle;

        lock (JobPool)
        {
            // if we're already done with this job; we don't care about signals.
            if (JobPool.IsComplete(jobID)) return;

            // increments a subscription counter. This ensures that the job that completes this will ping the handle and won't dispose it yet.
            // If we didn't have this, there would be a race condition between multiple threads which wait for a job's completion.
            handle = JobPool.AwaitJob(jobID);
        }

        handle.WaitOne();

        lock (JobPool)
        {
            // Return to pool. Ensures that nobody else is subscribed first, so this will always be the last person to have subscribed to a handle.
            // Once the last one of these returns, we can recycle the handle and the jobID
            JobPool.SetJob(jobID);
        }
    }

    [Conditional("DEBUG")]
    private void CheckIfJobIsFlushed(JobId jobID)
    {
        lock (QueuedJobs)
        {
            foreach (var job in QueuedJobs)
            {
                if (job.JobID == jobID)
                {
                    throw new InvalidOperationException($"Cannot wait on a job that is not flushed to the workers! Call {nameof(Flush)} first.");
                }
            }
        }
    }


    /// <summary>
    /// Disposes all internals and notifies all threads to cancel.
    /// </summary>
    public void Dispose()
    {
        if (!IsMainThread)
        {
            throw new InvalidOperationException($"Can only call {nameof(Dispose)} from the thread that spawned the {nameof(JobScheduler)}!");
        }

        // notify all threads to cancel
        CancellationTokenSource.Cancel(false);
        // we only lock in debug mode for strict flushed-jobs checking within Complete()
#if DEBUG
        lock (QueuedJobs)
#endif
        {
            QueuedJobs.Clear();
        }
        MasterQueue.Clear();

        // notifier handles issues where threads might dispose this early for us
        // either this works and threads are signalled to go into shutdown mode, which eventually dispose..
        // or they somehow managed to 100% shutdown and dispose already, in which case his does nothing.
        _notifier.NotifyAll();
    }
}