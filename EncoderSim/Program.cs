using Microsoft.Azure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EncoderSim
{
    public class Program
    {
        static void Main(string[] args)
        {

            ProcessQueue();
            Console.ReadLine();
        }

        private static async void ProcessQueue()
        {
            // Retrieve storage account from connection string.
            var storageAccount = CloudStorageAccount.Parse(
                CloudConfigurationManager.GetSetting("StorageConnectionString"));

            // Create the queue client.
            var queueClient = storageAccount.CreateCloudQueueClient();
            var tableClient = storageAccount.CreateCloudTableClient();

            // Retrieve a reference to a container.
            var queue = queueClient.GetQueueReference("myqueue");
            // Create the table client.

            // Retrieve a reference to the table.
            var table = tableClient.GetTableReference("messages");

            // Create the table if it doesn't exist.
            table.CreateIfNotExists();
            // Create the queue if it doesn't already exist
            queue.CreateIfNotExists();

            while (true)
            {
                queue.FetchAttributes();
                if (queue.ApproximateMessageCount != null && queue.ApproximateMessageCount > 0)
                {
                    var msg = await queue.GetMessageAsync();
                  
                    if (msg != null)
                    {
                        var content = msg.AsString;
                        var row = new MessageEntity(Environment.MachineName);
                        row.Message = content;
                        var insertOperation = TableOperation.Insert(row);
                        // Execute the insert operation.
                        await table.ExecuteAsync(insertOperation);
                        await queue.DeleteMessageAsync(msg);
                    }
                }
                Thread.Sleep(10);
            }
        }
        public class MessageEntity : TableEntity
        {
            public MessageEntity(string id)
            {
                this.PartitionKey = id;
                this.RowKey = Guid.NewGuid().ToString();
                Node = id;
            }

            public MessageEntity() { }

            public string Message { get; set; }

            public string Node { get; set; }
        }
    }
}
