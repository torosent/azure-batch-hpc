using Microsoft.Azure;
using Microsoft.WindowsAzure.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QueueSim
{
    class Program
    {
        static void Main(string[] args)
        {
            ProcessQueue(1000);
            Console.ReadLine();
        }

        private static async void ProcessQueue(int length)
        {
            // Retrieve storage account from connection string.
            var storageAccount = CloudStorageAccount.Parse(
                CloudConfigurationManager.GetSetting("StorageConnectionString"));

            // Create the queue client.
            var queueClient = storageAccount.CreateCloudQueueClient();

            // Retrieve a reference to a container.
            var queue = queueClient.GetQueueReference("myqueue");
            // Create the table client.

            // Create the queue if it doesn't already exist
            queue.CreateIfNotExists();

            for (int i = 0; i < length; i++)
            {
                string msg = $"New message {Guid.NewGuid().ToString()}";
                await queue.AddMessageAsync(new Microsoft.WindowsAzure.Storage.Queue.CloudQueueMessage(msg));
            }



        }
    }
}
