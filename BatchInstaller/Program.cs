
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Batch;
using Microsoft.Azure.Batch.Auth;
using Microsoft.Azure.Batch.Common;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.Azure;



namespace BatchInstaller
{
    public class Program
    {
        // Update the Batch and Storage account credential strings below with the values unique to your accounts.
        // These are used when constructing connection strings for the Batch and Storage client objects.

        // Batch account credentials
        private static readonly string BatchAccountName = CloudConfigurationManager.GetSetting("BatchAccountName");
        private static readonly string BatchAccountKey = CloudConfigurationManager.GetSetting("BatchAccountKey");
        private static readonly string BatchAccountUrl = CloudConfigurationManager.GetSetting("BatchAccountUrl");


        private const string PoolId = "CloudPool";
        private const string JobId = "EncodingJobs";
        private const string appContainerName = "encodersim";
        private const string encoderZip = "wsc.zip";
        private static int targetVMs = 4;
        public static void Main(string[] args)
        {

            try
            {
                // Call the asynchronous version of the Main() method. This is done so that we can await various
                // calls to async methods within the "Main" method of this console application.
                MainAsync().Wait();
            }
            catch (AggregateException ae)
            {
                Console.WriteLine();
                Console.WriteLine("One or more exceptions occurred.");
                Console.WriteLine();

                PrintAggregateException(ae);
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine("Install complete, hit ENTER to exit...");
                Console.ReadLine();
            }
        }

        /// <summary>
        /// Provides an asynchronous version of the Main method, allowing for the awaiting of async method calls within.
        /// </summary>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task MainAsync()
        {

            // Construct the Storage account connection string
            var storageAccount = CloudStorageAccount.Parse(
              CloudConfigurationManager.GetSetting("StorageConnectionString"));


            // Create the blob client, for use in obtaining references to blob storage containers
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

            // Clean up Storage resources
            // await DeleteContainerAsync(blobClient, appContainerName);
            //  await DeleteContainerAsync(blobClient, inputContainerName);
            //   await DeleteContainerAsync(blobClient, outputContainerName);

            await CreateContainerIfNotExistAsync(blobClient, appContainerName);

            // Paths to the executable and its dependencies that will be executed by the tasks
            List<string> applicationFilePaths = new List<string>
            {
                "wsc.zip",
                "7z.exe",
                "7z.dll"
            };



            // Upload the application and its dependencies to Azure Storage. This is the application that will
            // process the data files, and will be executed by each of the tasks on the compute nodes.
            List<ResourceFile> applicationFiles = await UploadFilesToContainerAsync(blobClient, appContainerName, applicationFilePaths);


            // Obtain a shared access signature that provides write access to the output container to which
            // the tasks will upload their output.
            //   string outputContainerSasUrl = GetContainerSasUrl(blobClient, outputContainerName, SharedAccessBlobPermissions.Write);

            // Create a BatchClient. We'll now be interacting with the Batch service in addition to Storage
            BatchSharedKeyCredentials cred = new BatchSharedKeyCredentials(BatchAccountUrl, BatchAccountName, BatchAccountKey);
            using (BatchClient batchClient = BatchClient.Open(cred))
            {
                // Create the pool that will contain the compute nodes that will execute the tasks.
                // The ResourceFile collection that we pass in is used for configuring the pool's StartTask
                // which is executed each time a node first joins the pool (or is rebooted or reimaged).
                await CreatePoolIfNotExistAsync(batchClient, PoolId, applicationFiles);

                // Create the job that will run the tasks.
                await CreateJobAsync(batchClient, JobId, PoolId);

                // Add the tasks to the job. 
                await AddTasksAsync(batchClient, JobId);

            }
        }

        /// <summary>
        /// Creates a container with the specified name in Blob storage, unless a container with that name already exists.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name for the new container.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task CreateContainerIfNotExistAsync(CloudBlobClient blobClient, string containerName)
        {
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);

            if (await container.CreateIfNotExistsAsync())
            {
                Console.WriteLine("Container [{0}] created.", containerName);
            }
            else
            {
                Console.WriteLine("Container [{0}] exists, skipping creation.", containerName);
            }
        }

        /// <summary>
        /// Returns a shared access signature (SAS) URL providing the specified permissions to the specified container.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the container for which a SAS URL should be obtained.</param>
        /// <param name="permissions">The permissions granted by the SAS URL.</param>
        /// <returns>A SAS URL providing the specified access to the container.</returns>
        /// <remarks>The SAS URL provided is valid for 2 hours from the time this method is called. The container must
        /// already exist within Azure Storage.</remarks>
        private static string GetContainerSasUrl(CloudBlobClient blobClient, string containerName, SharedAccessBlobPermissions permissions)
        {
            // Set the expiry time and permissions for the container access signature. In this case, no start time is specified,
            // so the shared access signature becomes valid immediately
            SharedAccessBlobPolicy sasConstraints = new SharedAccessBlobPolicy
            {
                SharedAccessExpiryTime = DateTime.UtcNow.AddYears(2),
                Permissions = permissions
            };

            // Generate the shared access signature on the container, setting the constraints directly on the signature
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);
            string sasContainerToken = container.GetSharedAccessSignature(sasConstraints);

            // Return the URL string for the container, including the SAS token
            return String.Format("{0}{1}", container.Uri, sasContainerToken);
        }

        /// <summary>
        /// Uploads the specified files to the specified Blob container, returning a corresponding
        /// collection of <see cref="ResourceFile"/> objects appropriate for assigning to a task's
        /// <see cref="CloudTask.ResourceFiles"/> property.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the blob storage container to which the files should be uploaded.</param>
        /// <param name="filePaths">A collection of paths of the files to be uploaded to the container.</param>
        /// <returns>A collection of <see cref="ResourceFile"/> objects.</returns>
        private static async Task<List<ResourceFile>> UploadFilesToContainerAsync(CloudBlobClient blobClient, string inputContainerName, List<string> filePaths)
        {
            List<ResourceFile> resourceFiles = new List<ResourceFile>();

            foreach (string filePath in filePaths)
            {
                resourceFiles.Add(await UploadFileToContainerAsync(blobClient, inputContainerName, filePath));
            }

            return resourceFiles;
        }

        /// <summary>
        /// Uploads the specified file to the specified Blob container.
        /// </summary>
        /// <param name="inputFilePath">The full path to the file to upload to Storage.</param>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the blob storage container to which the file should be uploaded.</param>
        /// <returns>A <see cref="Microsoft.Azure.Batch.ResourceFile"/> instance representing the file within blob storage.</returns>
        private static async Task<ResourceFile> UploadFileToContainerAsync(CloudBlobClient blobClient, string containerName, string filePath)
        {
            Console.WriteLine("Uploading file {0} to container [{1}]...", filePath, containerName);

            string blobName = Path.GetFileName(filePath);

            CloudBlobContainer container = blobClient.GetContainerReference(containerName);
            CloudBlockBlob blobData = container.GetBlockBlobReference(blobName);
            await blobData.UploadFromFileAsync(filePath);

            // Set the expiry time and permissions for the blob shared access signature. In this case, no start time is specified,
            // so the shared access signature becomes valid immediately
            SharedAccessBlobPolicy sasConstraints = new SharedAccessBlobPolicy
            {
                SharedAccessExpiryTime = DateTime.UtcNow.AddYears(2),
                Permissions = SharedAccessBlobPermissions.Read
            };

            // Construct the SAS URL for blob
            string sasBlobToken = blobData.GetSharedAccessSignature(sasConstraints);
            string blobSasUri = String.Format("{0}{1}", blobData.Uri, sasBlobToken);

            return new ResourceFile(blobSasUri, blobName);
        }

        /// <summary>
        /// Creates a <see cref="CloudPool"/> with the specified id and configures its StartTask with the
        /// specified <see cref="ResourceFile"/> collection.
        /// </summary>
        /// <param name="batchClient">A <see cref="BatchClient"/>.</param>
        /// <param name="poolId">The id of the <see cref="CloudPool"/> to create.</param>
        /// <param name="resourceFiles">A collection of <see cref="ResourceFile"/> objects representing blobs within
        /// a Storage account container. The StartTask will download these files from Storage prior to execution.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task CreatePoolIfNotExistAsync(BatchClient batchClient, string poolId, IList<ResourceFile> resourceFiles)
        {
            CloudPool pool = null;
            try
            {
                Console.WriteLine("Creating pool [{0}]...", poolId);

                // Create the unbound pool. Until we call CloudPool.Commit() or CommitAsync(), no pool is actually created in the
                // Batch service. This CloudPool instance is therefore considered "unbound," and we can modify its properties.
                pool = batchClient.PoolOperations.CreatePool(
                    poolId: poolId,
                    targetDedicated: targetVMs,                                                         // 3 compute nodes
                    virtualMachineSize: "Standard_D3_V2",
                    virtualMachineConfiguration: new VirtualMachineConfiguration(new ImageReference("WindowsServer", "MicrosoftWindowsServer", "2016-Datacenter"), "batch.node.windows amd64"));

                //Ensure only 1 EncoderSim runs on each node
                pool.TaskSchedulingPolicy = new TaskSchedulingPolicy(ComputeNodeFillType.Pack);
                pool.MaxTasksPerComputeNode = 1;

                // Create and assign the StartTask that will be executed when compute nodes join the pool.
                // In this case, we copy the StartTask's resource files (that will be automatically downloaded
                // to the node by the StartTask) into the shared directory that all tasks will have access to.
                pool.StartTask = new StartTask
                {
                    // Specify a command line for the StartTask that copies the task application files to the
                    // node's shared directory. Every compute node in a Batch pool is configured with a number
                    // of pre-defined environment variables that can be referenced by commands or applications
                    // run by tasks.

                    // Since a successful execution of robocopy can return a non-zero exit code (e.g. 1 when one or
                    // more files were successfully copied) we need to manually exit with a 0 for Batch to recognize
                    // StartTask execution success.
                    CommandLine = $"cmd /c (robocopy %AZ_BATCH_TASK_WORKING_DIR% %AZ_BATCH_NODE_SHARED_DIR%) ^& IF %ERRORLEVEL% LEQ 1 exit 0",
                    ResourceFiles = resourceFiles,
                    WaitForSuccess = true,

                };

                await pool.CommitAsync();
            }
            catch (BatchException be)
            {
                // Swallow the specific error code PoolExists since that is expected if the pool already exists
                if (be.RequestInformation?.BatchError != null && be.RequestInformation.BatchError.Code == BatchErrorCodeStrings.PoolExists)
                {
                    Console.WriteLine("The pool {0} already existed when we tried to create it", poolId);
                    pool = await batchClient.PoolOperations.GetPoolAsync(PoolId);
                    if (pool.TargetDedicated != targetVMs)
                    {
                        Console.WriteLine("The pool {0} is resizing to {1}", poolId, targetVMs);
                        await pool.ResizeAsync(targetVMs);
                    }

                }
                else
                {
                    throw; // Any other exception is unexpected
                }
            }
        }

        /// <summary>
        /// Creates a job in the specified pool.
        /// </summary>
        /// <param name="batchClient">A <see cref="BatchClient"/>.</param>
        /// <param name="jobId">The id of the job to be created.</param>
        /// <param name="poolId">The id of the <see cref="CloudPool"/> in which to create the job.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
           private static async Task CreateJobAsync(BatchClient batchClient, string jobId, string poolId)
        {


            if (CheckIfJobExist(batchClient, jobId))
            {
                Console.Write("Job [{0}] already exists. Do you want to upgrade the entire cluster?", jobId);
                var answer =Console.ReadLine().ToLower();
                if (answer == "y")
                {
                    Console.WriteLine("Deleting job [{0}]...", jobId);
                    batchClient.JobOperations.DeleteJob(jobId);

                    while (CheckIfJobExist(batchClient, jobId))
                    {

                    }
                    System.Threading.Thread.Sleep(5000);
                    await CreateJob(batchClient, jobId, poolId);
                }
                else // Only create new tasks for new VM's
                {
                    var pool = await batchClient.PoolOperations.GetPoolAsync(PoolId);
                    if (targetVMs > pool.CurrentDedicated.Value)
                    {
                        targetVMs = pool.TargetDedicated.Value - pool.CurrentDedicated.Value; // Scale up
                    }
                    else
                    {
                        targetVMs = 0; // Scale down
                    }
                }
            }
            else
            {
                await CreateJob(batchClient, jobId, poolId);
            }
            

        }

        private static async Task CreateJob(BatchClient batchClient, string jobId, string poolId)
        {
            Console.WriteLine("Creating job [{0}]...", jobId);
            CloudJob job = batchClient.JobOperations.CreateJob();
            job.Id = jobId;

            job.PoolInformation = new PoolInformation { PoolId = poolId };
            job.UsesTaskDependencies = true;

            await job.CommitAsync();
        }

        private static bool CheckIfJobExist(BatchClient batchClient, string jobId)
        {
            var joblist = batchClient.JobOperations.ListJobs();
            if (joblist.Any(x => x.Id == jobId))
            {
                return true;
            }
            return false;
        }
        /// <summary>
        /// Creates tasks to process each of the specified input files, and submits them to the
        /// specified job for execution.
        /// </summary>
        /// <param name="batchClient">A <see cref="BatchClient"/>.</param>
        /// <param name="jobId">The id of the job to which the tasks should be added.</param>
        /// <param name="inputFiles">A collection of <see cref="ResourceFile"/> objects representing the input files to be
        /// processed by the tasks executed on the compute nodes.</param>
        /// <param name="outputContainerSasUrl">The shared access signature URL for the container within Azure Storage that
        /// will receive the output files created by the tasks.</param>
        /// <returns>A collection of the submitted tasks.</returns>
        private static async Task<List<CloudTask>> AddTasksAsync(BatchClient batchClient, string jobId)
        {

            // Create each of the tasks. Because we copied the task application to the
            // node's shared directory with the pool's StartTask, we can access it via
            // the shared directory on whichever node each task will run.


            // Create a collection to hold the tasks that we'll be adding to the job
            List<CloudTask> tasks = new List<CloudTask>();
            var pool = await batchClient.PoolOperations.GetPoolAsync(PoolId);
            targetVMs = pool.TargetDedicated.Value;
            for (int i = 0; i < targetVMs; i++)
            {
                Console.WriteLine("Adding {0} tasks to job [{1}]...", 2, jobId);
                string ziptaskId = $"ExtractZip-{Guid.NewGuid().ToString()}" ;
                string ziptaskCommandLine = String.Format($"cmd /c %AZ_BATCH_NODE_SHARED_DIR%\\7z.exe x -aoa -o%AZ_BATCH_NODE_SHARED_DIR% %AZ_BATCH_NODE_SHARED_DIR%\\{encoderZip}");
                CloudTask ziptask = new CloudTask(ziptaskId, ziptaskCommandLine);
                tasks.Add(ziptask);


                string taskId = $"EncoderSim-{Guid.NewGuid().ToString()}";
                string taskCommandLine = String.Format($"cmd /c %AZ_BATCH_NODE_SHARED_DIR%\\{appContainerName}\\EncoderSim.exe");
                CloudTask task = new CloudTask(taskId, taskCommandLine);

                task.DependsOn = TaskDependencies.OnIds(ziptaskId);
                tasks.Add(task);
            }


            // Add the tasks as a collection opposed to a separate AddTask call for each. Bulk task submission
            // helps to ensure efficient underlying API calls to the Batch service.
            await batchClient.JobOperations.AddTaskAsync(jobId, tasks);

            return tasks;
        }


        /// <summary>
        /// Processes all exceptions inside an <see cref="AggregateException"/> and writes each inner exception to the console.
        /// </summary>
        /// <param name="aggregateException">The <see cref="AggregateException"/> to process.</param>
        public static void PrintAggregateException(AggregateException aggregateException)
        {
            // Flatten the aggregate and iterate over its inner exceptions, printing each
            foreach (Exception exception in aggregateException.Flatten().InnerExceptions)
            {
                Console.WriteLine(exception.ToString());
                Console.WriteLine();
            }
        }
    }
}
