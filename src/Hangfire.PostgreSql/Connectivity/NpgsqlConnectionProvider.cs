﻿using Npgsql;

namespace Hangfire.PostgreSql.Connectivity
{
    internal sealed class NpgsqlConnectionProvider : IConnectionProvider
    {
        private readonly string _connectionString;

        public NpgsqlConnectionProvider(string connectionString)
        {
            _connectionString = connectionString;
        }

        public ConnectionHolder AcquireConnection()
        {
            var connection = new NpgsqlConnection(_connectionString);
            return new ConnectionHolder(connection, holder => holder.Connection.Dispose());
        }

        public void Dispose()
        {
        }
    }
}
