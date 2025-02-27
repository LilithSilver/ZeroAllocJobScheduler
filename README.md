# ZeroAllocJobScheduler
[![Maintenance](https://img.shields.io/badge/Maintained%3F-yes-green.svg?style=for-the-badge)](https://GitHub.com/Naereen/StrapDown.js/graphs/commit-activity)
[![Nuget](https://img.shields.io/nuget/v/ZeroAllocJobScheduler?style=for-the-badge)](https://www.nuget.org/packages/ZeroAllocJobScheduler/)
[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg?style=for-the-badge)](https://opensource.org/licenses/Apache-2.0)
![C#](https://img.shields.io/badge/c%23-%23239120.svg?style=for-the-badge&logo=c-sharp&logoColor=white)

A high-performance alloc-free C# job scheduler.  
Schedules and executes jobs on a set of worker threads with automatic pooling of internal handles.

# Usage

```csharp

public class HeavyCalculation : IJob
{
  public void Execute()
  {
    Thread.Sleep(50);  // Simulate heavy work
    Console.WriteLine("Done");
  }
}

// Create a new Scheduler, which you should keep the lifetime of your program. This is the only API call that will allocate or generate garbage.
var scheduler = new JobScheduler(new JobScheduler.Config()
{
    // Names the process "MyProgram0", "MyProgram1", etc.
    ThreadPrefixName = "MyProgram",

    // Automatically chooses threads based on your processor count
    ThreadCount = 0,

    // The amount of jobs that can exist in the queue at once without the scheduler spontaneously allocating and generating garbage.
    // Past this number, the scheduler is no longer Zero-Alloc!
    // Higher numbers slightly decrease performance and increase memory consumption, so keep this on the lowest possible end for your application.
    MaxExpectedConcurrentJobs = 64,

    // Enables or disables strict allocation mode: if more jobs are scheduled at once than MaxExpectedConcurrentJobs, it throws an error.
    // Not recommended for production code, but good for debugging allocation issues.
    StrictAllocationMode = false,
});

// You need to pool/create jobs by yourself. This will, of course, allocate, so cache and reuse the jobs.
var firstJob = new HeavyCalculation();  
var firstHandle = scheduler.Schedule(firstJob); // Schedules job locally

scheduler.Flush();                              // Dispatches all scheduled jobs to the worker threads
firstHandle.Complete();                         // Blocks the thread until the job is complete.

// Call Dispose at program exit, which shuts down all worker threads
scheduler.Dispose();                
```

# Dependencies

To set a sequential dependency on a job, simply pass a created `JobHandle` to `JobScheduler.Schedule(job, dependency)`.

```csharp
var handle1 = scheduler.Schedule(job1);
var handle2 = scheduler.Schedule(job2, handle1);    // job2 will only begin execution once job1 is complete!
scheduler.Flush();
```

# Multiple dependencies

Use `Scheduler.CombineDependencies(JobHandle[] handles)` to get a new handle that depends on the handles in parallel. That handle can then be passed into future `Schedule` call as a dependency itself!

```csharp
// You must create the array of handles, and handle caching/storage yourself.
JobHandle[] handles = new JobHandle[2];

handles[0] = Scheduler.Schedule(job1);
handles[1] = Scheduler.Schedule(job2);
JobHandle combinedHandle = Scheduler.CombineDependencies(handles);          // Combines all handles into the array into one

var dependantHandle = Scheduler.Schedule(job3, combinedHandle);             // job3 now depends on job1 and job2.
                                                                            // job1 and job2 can Complete() in parallel, but job3 can only run once both are complete.

dependantHandle.Complete();                                                 // Blocks the main thread until all three tasks are complete.
```


# Bulk complete

Rather than using `CombineDependencies()`, if you just need to block the main thread until a list of handles are complete, you can use this syntax:

```csharp
JobHandle.CompleteAll(JobHandle[] handles);                     // Waits for all JobHandles to finish, and blocks the main thread until they each complete (in any order)
JobHandle.CompleteAll(List<JobHandle> handles);
```

Or, if you don't want to maintain a list or array, you can just call `handle.Complete()` on all your handles, in any order.
