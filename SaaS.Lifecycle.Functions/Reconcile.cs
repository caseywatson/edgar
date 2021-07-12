using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SaaS.Lifecycle.Functions.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace SaaS.Lifecycle.Functions
{
    public static class Reconcile
    {
        [FunctionName("Reconcile")]
        public static async Task Run(
            [TimerTrigger("0 */5 * * * *")] TimerInfo myTimer,
            [EventGrid(TopicEndpointUri = "EventGridEndpoint", TopicKeySetting = "EventGridKey")] IAsyncCollector<EventGridEvent> eventCollector,
            ILogger log)
        {
            try
            {
                // Grab the settings we're going to need to do this...

                var pat = Environment.GetEnvironmentVariable("GitHubPat");
                var storageConnString = Environment.GetEnvironmentVariable("StorageConnectionString");
                var containerName = Environment.GetEnvironmentVariable("OperationStorageContainerName");
                var repoOwner = Environment.GetEnvironmentVariable("repoOwner");

                // Let's go see what operations are pending...

                var serviceClient = new BlobServiceClient(storageConnString);
                var containerClient = serviceClient.GetBlobContainerClient(containerName);
                var opBlobs = new List<BlobItem>();
                var opBlobPages = containerClient.GetBlobsAsync().AsPages();

                await foreach (Page<BlobItem> opBlobPage in opBlobPages)
                {
                    // Presumably, these are all pending operations...

                    opBlobs.AddRange(opBlobPage.Values);
                }

                var opBlobsByRepo = (from opBlob in opBlobs
                                     let nameParts = opBlob.Name.Trim().Split('/') // Blob name should be [repo-name/run-id]...
                                     where (nameParts.Length == 2) && nameParts.All(np => !string.IsNullOrEmpty(np)) // Make sure that both parts are there, then...
                                     select new { RepoName = nameParts[0], OpBranchName = nameParts[1], Blob = opBlob }) // Get the repo name, run ID, and the blob itself, then...                                                                              
                                     .GroupBy(g => g.RepoName) // Group by repo name as this is how we'll work them, then...
                                     .ToDictionary(d => d.Key, d => d.ToDictionary(a => a.OpBranchName, a => a.Blob)); // Collapse it all down into a nested dictionary we can work with.

                if (opBlobsByRepo.Any())
                {
                    var httpClient = CreateGitHubHttpClient(pat);

                    // For each applicable repo...

                    foreach (var repoName in opBlobsByRepo.Keys)
                    {
                        // Let's get the latest completed workflow runs for this repo...

                        var repoRuns = await httpClient.GetRepoRunsAsync(repoOwner, repoName);

                        if (repoRuns.Runs.Any())
                        {
                            // Try to match each operation run ID to a GitHub run ID...

                            foreach (var operationId in opBlobsByRepo[repoName].Keys)
                            {
                                var run = repoRuns.Runs.FirstOrDefault(r => r.BranchName == operationId);

                                if (run != null)
                                {
                                    // Score! We found a match!

                                    try
                                    {
                                        // Try to reoncile the operation and run...

                                        var opBlobItem = opBlobsByRepo[repoName][operationId];
                                        var blobClient = containerClient.GetBlobClient(opBlobItem.Name);
                                        var operation = await blobClient.GetOperationAsync();
                                        var operationEvent = ReconcileOperation(operation, run);

                                        await blobClient.ArchiveOperationBlob();
                                        await httpClient.ArchiveOperationBranch(operation, repoOwner);
                                        await eventCollector.AddAsync(operationEvent);
                                    }
                                    catch (Exception ex)
                                    {
                                        // We can't reconcile this run so we'll just move to the next one...

                                        log.LogError(ex, $"Run [{repoName}/{run.RunId}] reconciliation has failed. See exception for details.");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Something _really_ broke. Log the exception and hope things go smoother next cycle. Even if we have a "poison operation",
                // it will eventually time out and expire. Don't think we can get into the position of having a "poison run." If something is
                // really, really wrong and this keeps on spinning, we should be alerted by these errors...

                log.LogError(ex, "An error occurred while attempting operation/run reconciliation. See exception for details.");

                throw;
            }
        }

        private static HttpClient CreateGitHubHttpClient(string pat)
        {
            var httpClient = new HttpClient { BaseAddress = new Uri("https://api.github.com") };

            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.mercy-preview+json");
            httpClient.DefaultRequestHeaders.Add("Authorization", $"token {pat}");

            return httpClient;
        }

        private static async Task<RepoRuns> GetRepoRunsAsync(this HttpClient httpClient, string repoOwner, string repoName)
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"/repos/{repoOwner}/{repoName}/actions/runs");
            var httpResponse = await httpClient.SendAsync(httpRequest);

            httpResponse.EnsureSuccessStatusCode();

            var httpContent = await httpResponse.Content.ReadAsStringAsync();

            return JsonConvert.DeserializeObject<RepoRuns>(httpContent);
        }

        private static async Task<Models.Operation> GetOperationAsync(this BlobClient opBlobClient)
        {
            var blobContent = await opBlobClient.DownloadContentAsync();
            var blobString = Encoding.UTF8.GetString(blobContent.Value.Content.ToArray());

            return JsonConvert.DeserializeObject<Models.Operation>(blobString);
        }

        private static EventGridEvent ReconcileOperation(Models.Operation operation, Run run) =>
             run.Conclusion switch
             {
                 Models.Run.Conclusions.Failure => ToEventGridEvent(operation, run, EventTypeNames.SubscriptionConfigurationFailed),
                 Models.Run.Conclusions.Success => ToEventGridEvent(operation, run, EventTypeNames.SubscriptionConfigured),
                 Models.Run.Conclusions.TimedOut => ToEventGridEvent(operation, run, EventTypeNames.SubscriptionConfigurationTimedOut),
                 _ => throw new Exception($"Unable to handle run conclusion [{run.Conclusion}].")
             };

        private static Task ArchiveOperationBlob(this BlobClient blobClient) => blobClient.DeleteAsync();

        private static async Task ArchiveOperationBranch(this HttpClient httpClient, Models.Operation operation, string repoOwner)
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Delete,
                $"/repos/{repoOwner}/{operation.RepoName}/git/refs/heads/{operation.OperationId}");

            var httpResponse = await httpClient.SendAsync(httpRequest);

            httpResponse.EnsureSuccessStatusCode();
        }

        private static EventGridEvent ToEventGridEvent(Models.Operation operation, Run run, string eventTypeName) =>
            new EventGridEvent
            {
                Data = new OperationEvent(operation, run),
                DataVersion = OperationEvent.DataVersion,
                EventTime = DateTime.UtcNow,
                EventType = eventTypeName,
                Id = Guid.NewGuid().ToString(),
                Subject = $"/saas/tenants/{operation.TenantId}/subscriptions/{operation.SubscriptionId}"
            };
    }
}
