// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Samples.Common;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Sql.Models;
using System.Collections.Generic;
using System;

namespace ManageSqlWithRecoveredOrRestoredDatabase
{
    public class Program
    {
        private static ResourceIdentifier? _resourceGroupId = null;

        /**
         * Azure SQL sample for managing point in time restore and recover a deleted SQL Database -
         *  - Create a SQL Server with two database from a pre-existing sample.
         *  - Create a new database from a point in time restore
         *  - Delete a database then restore it from a recoverable dropped database automatic backup
         *  - Delete databases and SQL Server
         */
        public static async Task RunSample(ArmClient client)
        {
            try
            {
                //Get default subscription
                SubscriptionResource subscription = await client.GetDefaultSubscriptionAsync();

                //Create a resource group in the EastUS region
                string rgName = Utilities.CreateRandomName("rgSQLServer");
                Utilities.Log("Creating resource group...");
                var rgLro = await subscription.GetResourceGroups().CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(AzureLocation.EastUS));
                ResourceGroupResource resourceGroup = rgLro.Value;
                _resourceGroupId = resourceGroup.Id;
                Utilities.Log($"Created a resource group with name: {resourceGroup.Data.Name} ");

                // ============================================================
                // Create a SQL Server with two databases from a sample.
                Utilities.Log("Creating a SQL Server with two databases from a sample.");
                string sqlServerName = Utilities.CreateRandomName("sqlserver");
                Utilities.Log("Creating SQL Server...");
                string sqlAdmin = "sqladmin" + sqlServerName;
                string sqlAdminPwd = Utilities.CreatePassword();
                SqlServerData sqlData = new SqlServerData(AzureLocation.EastUS)
                {
                    AdministratorLogin = sqlAdmin,
                    AdministratorLoginPassword = sqlAdminPwd
                };
                var sqlServerLro = await resourceGroup.GetSqlServers().CreateOrUpdateAsync(WaitUntil.Completed, sqlServerName, sqlData);
                SqlServerResource sqlServer = sqlServerLro.Value;
                Utilities.Log($"Created a SQL Server with name: {sqlServer.Data.Name} ");

                Utilities.Log("Creating first database...");
                string dbToDeleteName = Utilities.CreateRandomName("db-to-delete");
                SqlDatabaseData DBData = new SqlDatabaseData(AzureLocation.EastUS) { };
                SqlDatabaseResource dbToDelete = (await sqlServer.GetSqlDatabases().CreateOrUpdateAsync(WaitUntil.Completed, dbToDeleteName, DBData)).Value;
                Utilities.Log($"Created first database with name: {dbToDelete.Data.Name} ");

                Utilities.Log("Creating second database...");
                string dbToRestoreName = Utilities.CreateRandomName("db-to-restore");
                SqlDatabaseResource dbToRestore = (await sqlServer.GetSqlDatabases().CreateOrUpdateAsync(WaitUntil.Completed, dbToRestoreName, DBData)).Value;
                Utilities.Log($"Created second database with name: {dbToRestore.Data.Name} ");

                //Sleep for 5 minutes to allow for the service to be aware of the new server and databases

                Utilities.Log("Sleep for 5 minutes to allow for the service to be aware of the new server and databases");
                Thread.Sleep(TimeSpan.FromMinutes(5));

                // ============================================================
                //Loop until a point in time restore is available. 
                Utilities.Log("Loop until a point in time restore is available.");

                int retries = 50;
                while (retries > 0 && dbToRestore.GetSqlServerDatabaseRestorePoints().ToList().Count == 0)
                {
                    retries--;
                    // Sleep for about 3 minutes
                    Utilities.Log("get the point in time restore retry");
                    Thread.Sleep(TimeSpan.FromMinutes(3));
                }
                if (retries == 0)
                {
                    return;
                }

                Utilities.Log("list the restorepoint of database");

                // Restore point might not be ready right away and we will have to wait for it.
                Utilities.Log("Restore point might not be ready right away and we will have to wait for it");
                long waitForRestoreToBeReady = (long)((System.TimeSpan)dbToRestore.GetSqlServerDatabaseRestorePoints().ToList()[0].Data.EarliestRestoreOn.GetValueOrDefault().Subtract(DateTime.Now.ToUniversalTime())).TotalMilliseconds + 300000;
                if (waitForRestoreToBeReady > 0)
                {
                    Thread.Sleep(Convert.ToInt32(waitForRestoreToBeReady));
                }

                var getRestorePointTime = dbToRestore.GetSqlServerDatabaseRestorePoints().ToList()[0];
                Utilities.Log("Creating a PointInTimeRestore database...");
                var dbRestorePointData = new SqlDatabaseData(getRestorePointTime.Data.Location ?? AzureLocation.EastUS)// When createMode is PointInTimeRestore, sourceResourceId must be the resource ID of the existing database or existing sql pool, and restorePointInTime must be specified.
                {
                    CreateMode = SqlDatabaseCreateMode.PointInTimeRestore,
                    SourceResourceId = dbToRestore.Id,
                    RestorePointInTime = getRestorePointTime.Data.EarliestRestoreOn
                };
                string dbRestorePointName = Utilities.CreateRandomName("db-restore-pit");
                var dbRestorePoint = (await sqlServer.GetSqlDatabases().CreateOrUpdateAsync(WaitUntil.Completed, dbRestorePointName, dbRestorePointData)).Value;
                Utilities.Log($"Created a PointInTimeRestore database with name: {dbRestorePoint.Data.Name}");
                await dbRestorePoint.DeleteAsync(WaitUntil.Completed);

                // ============================================================
                // Delete the database than loop until the restorable dropped database backup is available.
                Utilities.Log("Deleting the database than loop until the restorable dropped database backup is available.");

                await dbToDelete.DeleteAsync(WaitUntil.Completed);
                int dropretries = 24;
                while (dropretries > 0 && sqlServer.GetRestorableDroppedDatabases().ToList().Count == 0)
                {
                    dropretries--;
                    // Sleep for about 5 minutes
                    Thread.Sleep(TimeSpan.FromMinutes(5));
                }
                var restorableDroppedDatabase = sqlServer.GetRestorableDroppedDatabases().ToList()[0];
                Utilities.Log("Restore a restorable dropped database...");
                var restoredata = new SqlDatabaseData(restorableDroppedDatabase.Data.Location)// When createMode is Restore, sourceResourceId must be the resource ID of restorable dropped database or restorable dropped sql pool.
                {
                    CreateMode = SqlDatabaseCreateMode.Restore,
                    SourceResourceId = restorableDroppedDatabase.Data.Id,
                    MaxSizeBytes = restorableDroppedDatabase.Data.MaxSizeBytes,
                    Tags =
                    {
                        ["key1"]="restorableDroppedDatabase"
                    }
                };
                var dbRestoreDeletedName = Utilities.CreateRandomName("db-restore-deleted");
                var dbRestoreDeleted = (await sqlServer.GetSqlDatabases().CreateOrUpdateAsync(WaitUntil.Completed, dbRestoreDeletedName, restoredata)).Value;
                Utilities.Log($"Restored a database with name {dbRestoreDeleted.Data.Name}");

                // Delete databases
                await dbToRestore.DeleteAsync(WaitUntil.Completed);
                await dbRestoreDeleted.DeleteAsync(WaitUntil.Completed);

                // Delete the SQL Server.
                Utilities.Log("Deleting a Sql Server");
                await sqlServer.DeleteAsync(WaitUntil.Completed);

            }
            finally
            {
                try
                {
                    if (_resourceGroupId is not null)
                    {
                        Utilities.Log($"Deleting Resource Group...");
                        await client.GetResourceGroupResource(_resourceGroupId).DeleteAsync(WaitUntil.Completed);
                        Utilities.Log($"Deleted Resource Group: {_resourceGroupId.Name}");
                    }
                }
                catch (Exception e)
                {
                    Utilities.Log(e);
                }
            }
        }
        public static async Task Main(string[] args)
        {
            try
            {
                //=================================================================
                // Authenticate
                var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
                var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
                var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
                var subscription = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
                ClientSecretCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                ArmClient client = new ArmClient(credential, subscription);

                await RunSample(client);
            }
            catch (Exception e)
            {
                Utilities.Log(e.ToString());
            }
        }
    }
}
