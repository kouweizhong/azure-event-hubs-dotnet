﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.EventHubs.Processor
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.EventHubs.Primitives;

    class PartitionManager
    {
        readonly EventProcessorHost host;
        readonly ConcurrentDictionary<string, PartitionPump> partitionPumps;

        IList<string> partitionIds;
        CancellationTokenSource cancellationTokenSource;
        Task runTask;

        internal PartitionManager(EventProcessorHost host)
        {
            this.host = host;
            this.cancellationTokenSource = new CancellationTokenSource();
            this.partitionPumps = new ConcurrentDictionary<string, PartitionPump>();
        }

        public async Task<IEnumerable<string>> GetPartitionIdsAsync()
        {
            if (this.partitionIds == null)
            {
                EventHubClient eventHubClient = null;
                try
                {
                    eventHubClient = this.host.CreateEventHubClient();
                    eventHubClient.WebProxy = this.host.EventProcessorOptions.WebProxy;
                    var runtimeInfo = await eventHubClient.GetRuntimeInformationAsync().ConfigureAwait(false);
                    this.partitionIds = runtimeInfo.PartitionIds.ToList();
                }
                catch (Exception e)
                {
                    throw new EventProcessorConfigurationException("Encountered error while fetching the list of EventHub PartitionIds", e);
                }
                finally
                {
                    if (eventHubClient != null)
                    {
                        await eventHubClient.CloseAsync().ConfigureAwait(false);
                    }
                }

                ProcessorEventSource.Log.EventProcessorHostInfo(this.host.HostName, $"PartitionCount: {this.partitionIds.Count}");
            }

            return this.partitionIds;
        }

        public async Task StartAsync()
        {
            if (this.runTask != null)
            {
                throw new InvalidOperationException("A PartitionManager cannot be started multiple times.");
            }

            await this.InitializeStoresAsync().ConfigureAwait(false);

            this.runTask = this.RunAsync();
        }

        public async Task StopAsync()
        {
            this.cancellationTokenSource.Cancel();
            var localRunTask = this.runTask;
            if (localRunTask != null)
            {
                await localRunTask.ConfigureAwait(false);
            }

            // once it is closed let's reset the task
            this.runTask = null;
            this.cancellationTokenSource = new CancellationTokenSource();
        }

        async Task RunAsync()
        {
            try
            {
                await this.RunLoopAsync(this.cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                ProcessorEventSource.Log.EventProcessorHostError(this.host.HostName, "Exception from partition manager main loop, shutting down", e.ToString());
                this.host.EventProcessorOptions.NotifyOfException(this.host.HostName, "N/A", e, EventProcessorHostActionStrings.PartitionManagerMainLoop);
            }

            try
            {
                // Cleanup
                ProcessorEventSource.Log.EventProcessorHostInfo(this.host.HostName, "Shutting down all pumps");
                await this.RemoveAllPumpsAsync(CloseReason.Shutdown).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                ProcessorEventSource.Log.EventProcessorHostError(this.host.HostName, "Failure during shutdown", e.ToString());
                this.host.EventProcessorOptions.NotifyOfException(this.host.HostName, "N/A", e, EventProcessorHostActionStrings.PartitionManagerCleanup);
            }
        }

        async Task InitializeStoresAsync() //throws InterruptedException, ExecutionException, ExceptionWithAction
        {
            // Make sure the lease store exists
            ILeaseManager leaseManager = this.host.LeaseManager;
            if (!await leaseManager.LeaseStoreExistsAsync().ConfigureAwait(false))
            {
                await RetryAsync(() => leaseManager.CreateLeaseStoreIfNotExistsAsync(), null, "Failure creating lease store for this Event Hub, retrying",
                        "Out of retries creating lease store for this Event Hub", EventProcessorHostActionStrings.CreatingLeaseStore, 5).ConfigureAwait(false);
            }
            // else
            //	lease store already exists, no work needed

            // Now make sure the leases exist
            foreach (string id in await this.GetPartitionIdsAsync().ConfigureAwait(false))
            {
                await RetryAsync(() => leaseManager.CreateLeaseIfNotExistsAsync(id), id, "Failure creating lease for partition, retrying",
                        "Out of retries creating lease for partition", EventProcessorHostActionStrings.CreatingLease, 5).ConfigureAwait(false);
            }

            // Make sure the checkpoint store exists
            ICheckpointManager checkpointManager = this.host.CheckpointManager;
            if (!await checkpointManager.CheckpointStoreExistsAsync().ConfigureAwait(false))
            {
                await RetryAsync(() => checkpointManager.CreateCheckpointStoreIfNotExistsAsync(), null, "Failure creating checkpoint store for this Event Hub, retrying",
                        "Out of retries creating checkpoint store for this Event Hub", EventProcessorHostActionStrings.CreatingCheckpointStore, 5).ConfigureAwait(false);
            }
            // else
            //	checkpoint store already exists, no work needed

            // Now make sure the checkpoints exist
            foreach (string id in await this.GetPartitionIdsAsync().ConfigureAwait(false))
            {
                await RetryAsync(() => checkpointManager.CreateCheckpointIfNotExistsAsync(id), id, "Failure creating checkpoint for partition, retrying",
                        "Out of retries creating checkpoint blob for partition", EventProcessorHostActionStrings.CreatingCheckpoint, 5).ConfigureAwait(false);
            }
        }

        // Throws if it runs out of retries. If it returns, action succeeded.
        async Task RetryAsync(Func<Task> lambda, string partitionId, string retryMessage, string finalFailureMessage, string action, int maxRetries) // throws ExceptionWithAction
        {
            Exception finalException = null;
            bool createdOK = false;
            int retryCount = 0;
            do
            {
                try
                {
                    await lambda().ConfigureAwait(false);
                    createdOK = true;
                }
                catch (Exception ex)
                {
                    if (partitionId != null)
                    {
                        ProcessorEventSource.Log.PartitionPumpWarning(this.host.HostName, partitionId, retryMessage, ex.ToString());
                    }
                    else
                    {
                        ProcessorEventSource.Log.EventProcessorHostWarning(this.host.HostName, retryMessage, ex.ToString());
                    }

                    finalException = ex;
                    retryCount++;
                }
            }
            while (!createdOK && (retryCount < maxRetries));

            if (!createdOK)
            {
                if (partitionId != null)
                {
                    ProcessorEventSource.Log.PartitionPumpError(this.host.HostName, partitionId, finalFailureMessage);
                }
                else
                {
                    ProcessorEventSource.Log.EventProcessorHostError(this.host.HostName, finalFailureMessage, null);
                }

                throw new EventProcessorRuntimeException(finalFailureMessage, action, finalException);
            }
        }

        async Task RunLoopAsync(CancellationToken cancellationToken) // throws Exception, ExceptionWithAction
        {
            var loopStopwatch = new Stopwatch();

            while (!cancellationToken.IsCancellationRequested)
            {
                // Mark start time so we can use the duration taken to calculate renew interval.
                loopStopwatch.Restart();

                ILeaseManager leaseManager = this.host.LeaseManager;
                Dictionary<string, Lease> allLeases = new Dictionary<string, Lease>();

                // Inspect all leases.
                // Acquire any expired leases.
                // Renew any leases that currently belong to us.
                var gettingAllLeases = leaseManager.GetAllLeases();
                var leasesOwnedByOthers = new List<Lease>();
                var renewLeaseTasks = new List<Task>();
                int ourLeaseCount = 0;

                // First thing is first, renew owned leases.
                foreach (Task<Lease> getLeaseTask in gettingAllLeases)
                {
                    try
                    {
                        var lease = await getLeaseTask.ConfigureAwait(false);
                        allLeases[lease.PartitionId] = lease;
                        if (lease.Owner == this.host.HostName)
                        {
                            ourLeaseCount++;
                            ProcessorEventSource.Log.PartitionPumpInfo(this.host.HostName, lease.PartitionId, "Trying to renew lease.");
                            renewLeaseTasks.Add(leaseManager.RenewLeaseAsync(lease).ContinueWith(renewResult =>
                            {
                                if (renewResult.IsFaulted || !renewResult.WaitAndUnwrapException())
                                {
                                    // Might have failed due to intermittent error or lease-lost.
                                    // Just log here, expired leases will be picked by same or another host anyway.
                                    ProcessorEventSource.Log.PartitionPumpError(this.host.HostName, lease.PartitionId, "Failed to renew lease.", renewResult.Exception?.Message);
                                    this.host.EventProcessorOptions.NotifyOfException(
                                        this.host.HostName,
                                        lease.PartitionId,
                                        renewResult.Exception,
                                        EventProcessorHostActionStrings.RenewingLease);
                                }
                            }, cancellationToken));
                        }
                        else
                        {
                            leasesOwnedByOthers.Add(lease);
                        }
                    }
                    catch (Exception e)
                    {
                        ProcessorEventSource.Log.EventProcessorHostError(this.host.HostName, "Failure during checking lease.", e.ToString());
                        this.host.EventProcessorOptions.NotifyOfException(this.host.HostName, "N/A", e, EventProcessorHostActionStrings.CheckingLeases);
                    }
                }

                // Wait until we are done with renewing our own leases here.
                // In theory, this should never throw, error are logged and notified in the renew tasks.
                await Task.WhenAll(renewLeaseTasks).ConfigureAwait(false);
                ProcessorEventSource.Log.EventProcessorHostInfo(this.host.HostName, "Lease renewal is finished.");

                // Check any expired leases that we can grab here.
                var checkLeaseTasks = new List<Task>();
                foreach (var possibleLease in allLeases.Values.Where(lease => lease.Owner != this.host.HostName))
                {
                    checkLeaseTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            if (await possibleLease.IsExpired().ConfigureAwait(false))
                            {
                                ProcessorEventSource.Log.PartitionPumpInfo(this.host.HostName, possibleLease.PartitionId, "Trying to acquire lease.");
                                if (await leaseManager.AcquireLeaseAsync(possibleLease).ConfigureAwait(false))
                                {
                                    ProcessorEventSource.Log.PartitionPumpInfo(this.host.HostName, possibleLease.PartitionId, "Acquired lease.");
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            ProcessorEventSource.Log.PartitionPumpError(this.host.HostName, possibleLease.PartitionId, "Failure during acquiring lease", e.ToString());
                            this.host.EventProcessorOptions.NotifyOfException(this.host.HostName, possibleLease.PartitionId, e, EventProcessorHostActionStrings.CheckingLeases);
                        }
                    }, cancellationToken));
                }

                await Task.WhenAll(checkLeaseTasks);

                // Grab more leases if available and needed for load balancing
                if (leasesOwnedByOthers.Count > 0)
                {
                    Lease stealThisLease = WhichLeaseToSteal(leasesOwnedByOthers, ourLeaseCount);
                    if (stealThisLease != null)
                    {
                        try
                        {
                            ProcessorEventSource.Log.PartitionPumpStealLeaseStart(this.host.HostName, stealThisLease.PartitionId);
                            if (await leaseManager.AcquireLeaseAsync(stealThisLease).ConfigureAwait(false))
                            {
                                // Succeeded in stealing lease
                                ProcessorEventSource.Log.PartitionPumpStealLeaseStop(this.host.HostName, stealThisLease.PartitionId);
                            }
                            else
                            {
                                ProcessorEventSource.Log.EventProcessorHostWarning(this.host.HostName,
                                    "Failed to steal lease for partition " + stealThisLease.PartitionId, null);
                            }
                        }
                        catch (Exception e)
                        {
                            ProcessorEventSource.Log.EventProcessorHostError(this.host.HostName,
                                "Exception during stealing lease for partition " + stealThisLease.PartitionId, e.ToString());
                            this.host.EventProcessorOptions.NotifyOfException(this.host.HostName,
                                stealThisLease.PartitionId, e, EventProcessorHostActionStrings.StealingLease);
                        }
                    }
                }

                // Update pump with new state of leases.
                foreach (string partitionId in allLeases.Keys)
                {
                    try
                    {
                        Lease updatedLease = allLeases[partitionId];
                        ProcessorEventSource.Log.EventProcessorHostInfo(this.host.HostName, $"Lease on partition {updatedLease.PartitionId} owned by {updatedLease.Owner}");
                        if (updatedLease.Owner == this.host.HostName)
                        {
                            await this.CheckAndAddPumpAsync(partitionId, updatedLease).ConfigureAwait(false);
                        }
                        else
                        {
                            await this.RemovePumpAsync(partitionId, CloseReason.LeaseLost).ConfigureAwait(false);
                        }
                    }
                    catch (Exception e)
                    {
                        ProcessorEventSource.Log.EventProcessorHostError(this.host.HostName, $"Exception during add/remove pump on partition {partitionId}", e.Message);
                        this.host.EventProcessorOptions.NotifyOfException(this.host.HostName, partitionId, e, EventProcessorHostActionStrings.PartitionPumpManagement);
                    }
                }

                try
                {
                    // Consider reducing the wait time with last lease-walkthrough's time taken.
                    var elapsedTime = loopStopwatch.Elapsed;
                    if (leaseManager.LeaseRenewInterval > elapsedTime)
                    {
                        await Task.Delay(leaseManager.LeaseRenewInterval.Subtract(elapsedTime), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (TaskCanceledException)
                {
                    // Bail on the async work if we are canceled.
                }
            }
        }

        async Task CheckAndAddPumpAsync(string partitionId, Lease lease)
        {
            PartitionPump capturedPump;
            if (this.partitionPumps.TryGetValue(partitionId, out capturedPump))
            {
                // There already is a pump. Make sure the pump is working and replace the lease.
                if (capturedPump.PumpStatus == PartitionPumpStatus.Errored || capturedPump.IsClosing)
                {
                    // The existing pump is bad. Remove it.
                    await RemovePumpAsync(partitionId, CloseReason.Shutdown).ConfigureAwait(false);
                }
                else
                {
                    // Pump is working, just replace the lease.
                    ProcessorEventSource.Log.PartitionPumpInfo(this.host.HostName, partitionId, "Updating lease for pump");
                    capturedPump.SetLease(lease);
                }
            }
            else
            {
                // No existing pump, create a new one.
                await CreateNewPumpAsync(partitionId, lease).ConfigureAwait(false);
            }
        }

        async Task CreateNewPumpAsync(string partitionId, Lease lease)
        {
            PartitionPump newPartitionPump = new EventHubPartitionPump(this.host, lease);
            await newPartitionPump.OpenAsync().ConfigureAwait(false);
            this.partitionPumps.TryAdd(partitionId, newPartitionPump); // do the put after start, if the start fails then put doesn't happen
            ProcessorEventSource.Log.PartitionPumpInfo(this.host.HostName, partitionId, "Created new PartitionPump");
        }

        async Task RemovePumpAsync(string partitionId, CloseReason reason)
        {
            PartitionPump capturedPump;
            if (this.partitionPumps.TryRemove(partitionId, out capturedPump))
            {
                if (!capturedPump.IsClosing)
                {
                    await capturedPump.CloseAsync(reason).ConfigureAwait(false);
                }
                // else, pump is already closing/closed, don't need to try to shut it down again
            }
            else
            {
                // PartitionManager main loop tries to remove pump for every partition that the host does not own, just to be sure.
                // Not finding a pump for a partition is normal and expected most of the time.
                ProcessorEventSource.Log.PartitionPumpInfo(this.host.HostName, partitionId, "No pump found to remove for this partition");
            }
        }

        Task RemoveAllPumpsAsync(CloseReason reason)
        {
            List<Task> tasks = new List<Task>();
            var keys = new List<string>(this.partitionPumps.Keys);
            foreach (string partitionId in keys)
            {
                tasks.Add(this.RemovePumpAsync(partitionId, reason));
            }

            return Task.WhenAll(tasks);
        }

        Lease WhichLeaseToSteal(List<Lease> stealableLeases, int haveLeaseCount)
        {
            IDictionary<string, int> countsByOwner = CountLeasesByOwner(stealableLeases);
            var biggestOwner = countsByOwner.OrderByDescending(o => o.Value).First();
            Lease stealThisLease = null;

            // If the number of leases is a multiple of the number of hosts, then the desired configuration is
            // that all hosts own the name number of leases, and the difference between the "biggest" owner and
            // any other is 0.
            //
            // If the number of leases is not a multiple of the number of hosts, then the most even configuration
            // possible is for some hosts to have (leases/hosts) leases and others to have ((leases/hosts) + 1).
            // For example, for 16 partitions distributed over five hosts, the distribution would be 4, 3, 3, 3, 3,
            // or any of the possible reorderings.
            //
            // In either case, if the difference between this host and the biggest owner is 2 or more, then the
            // system is not in the most evenly-distributed configuration, so steal one lease from the biggest.
            // If there is a tie for biggest, we pick whichever appears first in the list because
            // it doesn't really matter which "biggest" is trimmed down.
            //
            // Stealing one at a time prevents flapping because it reduces the difference between the biggest and
            // this host by two at a time. If the starting difference is two or greater, then the difference cannot
            // end up below 0. This host may become tied for biggest, but it cannot become larger than the host that
            // it is stealing from.

            if ((biggestOwner.Value - haveLeaseCount) >= 2)
            {
                stealThisLease = stealableLeases.First(l => l.Owner == biggestOwner.Key);
                ProcessorEventSource.Log.EventProcessorHostInfo(this.host.HostName, $"Proposed to steal lease for partition {stealThisLease.PartitionId} from {biggestOwner.Key}");
            }

            return stealThisLease;
        }

        Dictionary<string, int> CountLeasesByOwner(IEnumerable<Lease> leases)
        {
            var counts = leases.GroupBy(lease => lease.Owner).Select(group => new
            {
                Owner = group.Key,
                Count = group.Count()
            });

            // Log ownership mapping.
            foreach (var owner in counts)
            {
                ProcessorEventSource.Log.EventProcessorHostInfo(this.host.HostName, $"Host {owner.Owner} owns {owner.Count} leases");
            }

            ProcessorEventSource.Log.EventProcessorHostInfo(this.host.HostName, $"Total hosts in list: {counts.Count()}");

            return counts.ToDictionary(e => e.Owner, e => e.Count);
        }
    }
}