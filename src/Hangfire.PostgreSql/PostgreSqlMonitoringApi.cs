﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dapper;
using Hangfire.Common;
using Hangfire.PostgreSql.Entities;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Npgsql;

namespace Hangfire.PostgreSql
{
    internal class PostgreSqlMonitoringApi : IMonitoringApi
    {
        private readonly IPostgreSqlConnectionProvider _connectionProvider;
        private readonly PostgreSqlStorageOptions _options;
        private readonly PersistentJobQueueProviderCollection _queueProviders;

        public PostgreSqlMonitoringApi(
            IPostgreSqlConnectionProvider connection,
            PostgreSqlStorageOptions options,
            PersistentJobQueueProviderCollection queueProviders)
        {
            _connectionProvider = connection ?? throw new ArgumentNullException(nameof(connection));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _queueProviders = queueProviders ?? throw new ArgumentNullException(nameof(queueProviders));
        }

        public long ScheduledCount()
        {
            return UseConnection(connection =>
                GetNumberOfJobsByStateName(connection, ScheduledState.StateName));
        }

        public long EnqueuedCount(string queue)
        {
            return UseConnection(connection =>
            {
                var queueApi = GetQueueApi(connection, queue);
                var counters = queueApi.GetEnqueuedAndFetchedCount(queue);

                return counters.EnqueuedCount ?? 0;
            });
        }

        public long FetchedCount(string queue)
        {
            return UseConnection(connection =>
            {
                var queueApi = GetQueueApi(connection, queue);
                var counters = queueApi.GetEnqueuedAndFetchedCount(queue);

                return counters.FetchedCount ?? 0;
            });
        }

        public long FailedCount()
        {
            return UseConnection(connection =>
                GetNumberOfJobsByStateName(connection, FailedState.StateName));
        }

        public long ProcessingCount()
        {
            return UseConnection(connection =>
                GetNumberOfJobsByStateName(connection, ProcessingState.StateName));
        }

        public JobList<ProcessingJobDto> ProcessingJobs(int @from, int count)
        {
            return UseConnection(connection => GetJobs(
                connection,
                @from, count,
                ProcessingState.StateName,
                (sqlJob, job, stateData) => new ProcessingJobDto
                {
                    Job = job,
                    ServerId = stateData.ContainsKey("ServerId") ? stateData["ServerId"] : stateData["ServerName"],
                    StartedAt = JobHelper.DeserializeDateTime(stateData["StartedAt"]),
                }));
        }

        public JobList<ScheduledJobDto> ScheduledJobs(int @from, int count)
        {
            return UseConnection(connection => GetJobs(
                connection,
                @from, count,
                ScheduledState.StateName,
                (sqlJob, job, stateData) => new ScheduledJobDto
                {
                    Job = job,
                    EnqueueAt = JobHelper.DeserializeDateTime(stateData["EnqueueAt"]),
                    ScheduledAt = JobHelper.DeserializeDateTime(stateData["ScheduledAt"])
                }));
        }

        public IDictionary<DateTime, long> SucceededByDatesCount()
        {
            return UseConnection(connection =>
                GetTimelineStats(connection, "succeeded"));
        }

        public IDictionary<DateTime, long> FailedByDatesCount()
        {
            return UseConnection(connection =>
                GetTimelineStats(connection, "failed"));
        }

        public IList<ServerDto> Servers()
        {
            return UseConnection<IList<ServerDto>>(connection =>
            {
                var servers = connection.Query<Entities.Server>(
                        @"SELECT * FROM """ + _options.SchemaName + @""".""server""")
                    .ToList();

                var result = new List<ServerDto>();

                foreach (var server in servers)
                {
                    var data = JobHelper.FromJson<ServerData>(server.Data);
                    result.Add(new ServerDto
                    {
                        Name = server.Id,
                        Heartbeat = server.LastHeartbeat,
                        Queues = data.Queues,
                        StartedAt = data.StartedAt ?? DateTime.MinValue,
                        WorkersCount = data.WorkerCount
                    });
                }

                return result;
            });
        }

        public JobList<FailedJobDto> FailedJobs(int @from, int count)
        {
            return UseConnection(connection => GetJobs(
                connection,
                @from,
                count,
                FailedState.StateName,
                (sqlJob, job, stateData) => new FailedJobDto
                {
                    Job = job,
                    Reason = sqlJob.StateReason,
                    ExceptionDetails = stateData["ExceptionDetails"],
                    ExceptionMessage = stateData["ExceptionMessage"],
                    ExceptionType = stateData["ExceptionType"],
                    FailedAt = JobHelper.DeserializeNullableDateTime(stateData["FailedAt"])
                }));
        }

        public JobList<SucceededJobDto> SucceededJobs(int @from, int count)
        {
            return UseConnection(connection => GetJobs(
                connection,
                @from,
                count,
                SucceededState.StateName,
                (sqlJob, job, stateData) => new SucceededJobDto
                {
                    Job = job,
                    Result = stateData.ContainsKey("Result") ? stateData["Result"] : null,
                    TotalDuration = stateData.ContainsKey("PerformanceDuration") && stateData.ContainsKey("Latency")
                        ? (long?)long.Parse(stateData["PerformanceDuration"]) +
                          (long?)long.Parse(stateData["Latency"])
                        : null,
                    SucceededAt = JobHelper.DeserializeNullableDateTime(stateData["SucceededAt"])
                }));
        }

        public JobList<DeletedJobDto> DeletedJobs(int @from, int count)
        {
            return UseConnection(connection => GetJobs(
                connection,
                @from,
                count,
                DeletedState.StateName,
                (sqlJob, job, stateData) => new DeletedJobDto
                {
                    Job = job,
                    DeletedAt = JobHelper.DeserializeNullableDateTime(stateData["DeletedAt"])
                }));
        }

        public IList<QueueWithTopEnqueuedJobsDto> Queues()
        {
            return UseConnection<IList<QueueWithTopEnqueuedJobsDto>>(connection =>
            {
                var tuples = _queueProviders
                    .Select(x => x.GetJobQueueMonitoringApi(_connectionProvider))
                    .SelectMany(x => x.GetQueues(), (monitoring, queue) => new { Monitoring = monitoring, Queue = queue })
                    .OrderBy(x => x.Queue)
                    .ToArray();

                var result = new List<QueueWithTopEnqueuedJobsDto>(tuples.Length);

                foreach (var tuple in tuples)
                {
                    var enqueuedJobIds = tuple.Monitoring.GetEnqueuedJobIds(tuple.Queue, 0, 5);
                    var counters = tuple.Monitoring.GetEnqueuedAndFetchedCount(tuple.Queue);

                    result.Add(new QueueWithTopEnqueuedJobsDto
                    {
                        Name = tuple.Queue,
                        Length = counters.EnqueuedCount ?? 0,
                        Fetched = counters.FetchedCount,
                        FirstJobs = EnqueuedJobs(connection, enqueuedJobIds)
                    });
                }

                return result;
            });
        }

        public JobList<EnqueuedJobDto> EnqueuedJobs(string queue, int @from, int perPage)
        {
            return UseConnection(connection =>
            {
                var queueApi = GetQueueApi(connection, queue);
                var enqueuedJobIds = queueApi.GetEnqueuedJobIds(queue, @from, perPage);

                return EnqueuedJobs(connection, enqueuedJobIds);
            });
        }

        public JobList<FetchedJobDto> FetchedJobs(string queue, int @from, int perPage)
        {
            return UseConnection(connection =>
            {
                var queueApi = GetQueueApi(connection, queue);
                var fetchedJobIds = queueApi.GetFetchedJobIds(queue, @from, perPage);

                return FetchedJobs(connection, fetchedJobIds);
            });
        }

        public IDictionary<DateTime, long> HourlySucceededJobs()
        {
            return UseConnection(connection =>
                GetHourlyTimelineStats(connection, "succeeded"));
        }

        public IDictionary<DateTime, long> HourlyFailedJobs()
        {
            return UseConnection(connection =>
                GetHourlyTimelineStats(connection, "failed"));
        }

        public JobDetailsDto JobDetails(string jobId)
        {
            return UseConnection(connection =>
            {
                string sql = @"
SELECT ""id"" ""Id"", ""invocationdata"" ""InvocationData"", ""arguments"" ""Arguments"", ""createdat"" ""CreatedAt"", ""expireat"" ""ExpireAt"" 
FROM """ + _options.SchemaName + @""".""job"" 
WHERE ""id"" = @id;

SELECT ""jobid"" ""JobId"", ""name"" ""Name"", ""value"" ""Value"" from """ + _options.SchemaName +
                             @""".""jobparameter"" 
WHERE ""jobid"" = @id;

SELECT ""jobid"" ""JobId"", ""name"" ""Name"", ""reason"" ""Reason"", ""createdat"" ""CreatedAt"", ""data"" ""Data"" 
FROM """ + _options.SchemaName + @""".""state"" 
WHERE ""jobid"" = @id 
ORDER BY ""id"" DESC;
";
                using (var multi = connection.QueryMultiple(sql,
                    new { id = Convert.ToInt32(jobId, CultureInfo.InvariantCulture) }))
                {
                    var job = multi.Read<SqlJob>().SingleOrDefault();
                    if (job == null) return null;

                    var parameters = multi.Read<JobParameter>().ToDictionary(x => x.Name, x => x.Value);
                    var history =
                        multi.Read<SqlState>()
                            .ToList()
                            .Select(x => new StateHistoryDto
                            {
                                StateName = x.Name,
                                CreatedAt = x.CreatedAt,
                                Reason = x.Reason,
                                Data = JobHelper.FromJson<Dictionary<string, string>>(x.Data)
                            })
                            .ToList();

                    return new JobDetailsDto
                    {
                        CreatedAt = job.CreatedAt,
                        Job = DeserializeJob(job.InvocationData, job.Arguments),
                        History = history,
                        Properties = parameters
                    };
                }
            });
        }

        public long SucceededListCount()
        {
            return UseConnection(connection =>
                GetNumberOfJobsByStateName(connection, SucceededState.StateName));
        }

        public long DeletedListCount()
        {
            return UseConnection(connection =>
                GetNumberOfJobsByStateName(connection, DeletedState.StateName));
        }

        public StatisticsDto GetStatistics()
        {
            var sql = $@"
SELECT ""statename"" ""State"", COUNT(""id"") ""Count"" 
FROM ""{_options.SchemaName}"".""job""
WHERE ""statename"" IS NOT NULL
GROUP BY ""statename"";

SELECT COUNT(*) 
FROM ""{_options.SchemaName}"".""server"";

SELECT SUM(""value"") 
FROM ""{_options.SchemaName}"".""counter"" 
WHERE ""key"" = 'stats:succeeded';

SELECT SUM(""value"") 
FROM ""{_options.SchemaName}"".""counter"" 
WHERE ""key"" = 'stats:deleted';

SELECT COUNT(*) 
FROM ""{_options.SchemaName}"".""set"" 
WHERE ""key"" = 'recurring-jobs';
";
            return UseConnection(connection =>
            {
                var stats = new StatisticsDto();
                using (var gridReader = connection.QueryMultiple(sql))
                {
                    var countByStates = gridReader.Read().ToDictionary(x => x.State, x => x.Count);

                    long GetCountIfExists(string name) => countByStates.ContainsKey(name) ? countByStates[name] : 0;

                    stats.Enqueued = GetCountIfExists(EnqueuedState.StateName);
                    stats.Failed = GetCountIfExists(FailedState.StateName);
                    stats.Processing = GetCountIfExists(ProcessingState.StateName);
                    stats.Scheduled = GetCountIfExists(ScheduledState.StateName);

                    stats.Servers = gridReader.Read<long>().Single();

                    stats.Succeeded = gridReader.Read<long?>().SingleOrDefault() ?? 0;
                    stats.Deleted = gridReader.Read<long?>().SingleOrDefault() ?? 0;

                    stats.Recurring = gridReader.Read<long>().Single();
                }

                stats.Queues = _queueProviders
                    .SelectMany(x => x.GetJobQueueMonitoringApi(_connectionProvider).GetQueues())
                    .Count();

                return stats;
            });
        }

        private Dictionary<DateTime, long> GetHourlyTimelineStats(NpgsqlConnection connection, string type)
        {
            var endDate = DateTime.UtcNow;
            var dates = Enumerable.Range(0, 24).Select(i => endDate.AddHours(-i)).ToList();
            var keyMaps = dates.ToDictionary(x => $"stats:{type}:{x:yyyy-MM-dd-HH}", x => x);

            return GetTimelineStats(connection, keyMaps);
        }

        private Dictionary<DateTime, long> GetTimelineStats(NpgsqlConnection connection, string type)
        {
            var endDate = DateTime.UtcNow.Date;
            var dates = Enumerable.Range(0, 7).Select(i => endDate.AddDays(-i)).ToList();
            var keyMaps = dates.ToDictionary(x => $"stats:{type}:{x:yyyy-MM-dd}", x => x);

            return GetTimelineStats(connection, keyMaps);
        }

        private Dictionary<DateTime, long> GetTimelineStats(NpgsqlConnection connection,
            IDictionary<string, DateTime> keyMaps)
        {
            var query = $@"
SELECT ""key"", COUNT(""value"") AS ""count"" 
FROM ""{_options.SchemaName}"".""counter""
WHERE ""key"" = ANY (@keys)
GROUP BY ""key"";
";

            var valuesMap = connection.Query(
                    query,
                    new { keys = keyMaps.Keys.ToList() })
                .ToList()
                .ToDictionary(x => (string)x.key, x => (long)x.count);

            foreach (var key in keyMaps.Keys)
            {
                if (!valuesMap.ContainsKey(key)) valuesMap.Add(key, 0);
            }

            var result = new Dictionary<DateTime, long>();
            for (var i = 0; i < keyMaps.Count; i++)
            {
                var value = valuesMap[keyMaps.ElementAt(i).Key];
                result.Add(keyMaps.ElementAt(i).Value, value);
            }

            return result;
        }

        private IPersistentJobQueueMonitoringApi GetQueueApi(
            NpgsqlConnection connection,
            string queueName)
        {
            var provider = _queueProviders.GetProvider(queueName);
            var monitoringApi = provider.GetJobQueueMonitoringApi(_connectionProvider);

            return monitoringApi;
        }

        private T UseConnection<T>(Func<NpgsqlConnection, T> action)
        {
            using (var connectionHolder = _connectionProvider.AcquireConnection())
            {
                return action(connectionHolder.Connection);
            }
        }

        private JobList<EnqueuedJobDto> EnqueuedJobs(NpgsqlConnection connection, IEnumerable<int> jobIds)
        {
            string enqueuedJobsSql = @"
SELECT j.""id"" ""Id"", j.""invocationdata"" ""InvocationData"", j.""arguments"" ""Arguments"", j.""createdat"" ""CreatedAt"", j.""expireat"" ""ExpireAt"", s.""name"" ""StateName"", s.""reason"" ""StateReason"", s.""data"" ""StateData""
FROM """ + _options.SchemaName + @""".""job"" j
LEFT JOIN """ + _options.SchemaName + @""".""state"" s ON s.""id"" = j.""stateid""
LEFT JOIN """ + _options.SchemaName + @""".""jobqueue"" jq ON jq.""jobid"" = j.""id""
WHERE j.""id"" = ANY (@jobIds)
AND jq.""fetchedat"" IS NULL;
";

            var jobs = connection.Query<SqlJob>(
                    enqueuedJobsSql,
                    new { jobIds = jobIds.ToList() })
                .ToList();

            return DeserializeJobs(
                jobs,
                (sqlJob, job, stateData) => new EnqueuedJobDto
                {
                    Job = job,
                    State = sqlJob.StateName,
                    EnqueuedAt = sqlJob.StateName == EnqueuedState.StateName
                        ? JobHelper.DeserializeNullableDateTime(stateData["EnqueuedAt"])
                        : null
                });
        }

        private long GetNumberOfJobsByStateName(NpgsqlConnection connection, string stateName)
        {
            string sqlQuery = @"
SELECT COUNT(""id"") 
FROM """ + _options.SchemaName + @""".""job"" 
WHERE ""statename"" = @state;
";

            var count = connection.Query<long>(
                    sqlQuery,
                    new { state = stateName })
                .Single();

            return count;
        }

        private static Job DeserializeJob(string invocationData, string arguments)
        {
            var data = JobHelper.FromJson<InvocationData>(invocationData);
            data.Arguments = arguments;

            try
            {
                return data.Deserialize();
            }
            catch (JobLoadException)
            {
                return null;
            }
        }

        private JobList<TDto> GetJobs<TDto>(NpgsqlConnection connection, int @from, int count, string stateName,
            Func<SqlJob, Job, Dictionary<string, string>, TDto> selector)
        {
            string jobsSql = @"
SELECT j.""id"" ""Id"", j.""invocationdata"" ""InvocationData"", j.""arguments"" ""Arguments"", j.""createdat"" ""CreatedAt"", 
    j.""expireat"" ""ExpireAt"", NULL ""FetchedAt"", j.""statename"" ""StateName"", s.""reason"" ""StateReason"", s.""data"" ""StateData""
FROM """ + _options.SchemaName + @""".""job"" j
LEFT JOIN """ + _options.SchemaName + @""".""state"" s ON j.""stateid"" = s.""id""
WHERE j.""statename"" = @stateName 
ORDER BY j.""id""
LIMIT @count OFFSET @start;
";

            var jobs = connection.Query<SqlJob>(
                    jobsSql,
                    new { stateName = stateName, start = from, count = count })
                .ToList();

            return DeserializeJobs(jobs, selector);
        }

        private static JobList<TDto> DeserializeJobs<TDto>(
            ICollection<SqlJob> jobs,
            Func<SqlJob, Job, Dictionary<string, string>, TDto> selector)
        {
            var result = new List<KeyValuePair<string, TDto>>(jobs.Count);

            foreach (var job in jobs)
            {
                var stateData = JobHelper.FromJson<Dictionary<string, string>>(job.StateData);
                var dto = selector(job, DeserializeJob(job.InvocationData, job.Arguments), stateData);

                result.Add(new KeyValuePair<string, TDto>(
                    job.Id.ToString(), dto));
            }

            return new JobList<TDto>(result);
        }

        private JobList<FetchedJobDto> FetchedJobs(
            NpgsqlConnection connection,
            IEnumerable<int> jobIds)
        {
            string fetchedJobsSql = @"
SELECT j.""id"" ""Id"", j.""invocationdata"" ""InvocationData"", j.""arguments"" ""Arguments"", j.""createdat"" ""CreatedAt"", 
    j.""expireat"" ""ExpireAt"", jq.""fetchedat"" ""FetchedAt"", j.""statename"" ""StateName"", s.""reason"" ""StateReason"", s.""data"" ""StateData""
FROM """ + _options.SchemaName + @""".""job"" j
LEFT JOIN """ + _options.SchemaName + @""".""state"" s ON j.""stateid"" = s.""id""
LEFT JOIN """ + _options.SchemaName + @""".""jobqueue"" jq ON jq.""jobid"" = j.""id""
WHERE j.""id"" = ANY (@jobIds)
AND ""jq"".""fetchedat"" IS NOT NULL;
";

            var jobs = connection.Query<SqlJob>(
                    fetchedJobsSql,
                    new { jobIds = jobIds.ToList() })
                .ToList();

            var result = new List<KeyValuePair<string, FetchedJobDto>>(jobs.Count);

            foreach (var job in jobs)
            {
                result.Add(new KeyValuePair<string, FetchedJobDto>(
                    job.Id.ToString(),
                    new FetchedJobDto
                    {
                        Job = DeserializeJob(job.InvocationData, job.Arguments),
                        State = job.StateName,
                        FetchedAt = job.FetchedAt
                    }));
            }

            return new JobList<FetchedJobDto>(result);
        }
    }
}
