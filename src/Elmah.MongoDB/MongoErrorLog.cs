﻿using System;
using System.Collections;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Collections.Specialized;

namespace Elmah
{
    public class MongoErrorLog : ErrorLog
    {
        private readonly string _connectionString;
        private readonly string? _collectionName;
        private readonly int _maxDocuments;
        private readonly int _maxSize;

        private IMongoCollection<BsonDocument>? _collection;

        private const int MaxAppNameLength = 60;
        private const int DefaultMaxDocuments = int.MaxValue;
        private const int DefaultMaxSize = 100 * 1024 * 1024;   // in bytes (100mb)

        private static readonly object Sync = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoErrorLog"/> class
        /// using a dictionary of configured settings.
        /// </summary>
        public MongoErrorLog(IDictionary config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            var connectionString = GetConnectionString(config);

            //
            // If there is no connection string to use then throw an 
            // exception to abort construction.
            //

            if (connectionString.Length == 0)
                throw new ApplicationException("Connection string is missing for the Elmah Error Log.");

            _connectionString = connectionString;

            //
            // Set the application name as this implementation provides
            // per-application isolation over a single store.
            //

            var appName = (string)config["applicationName"] ?? string.Empty;

            if (appName.Length > MaxAppNameLength)
            {
                throw new ApplicationException(string.Format(
                  "Application name is too long. Maximum length allowed is {0} characters.",
                  MaxAppNameLength.ToString("N0")));
            }

            ApplicationName = appName;

            _collectionName = appName.Length > 0 ? "Elmah-" + appName : "Elmah";
            _maxDocuments = GetCollectionLimit(config);
            _maxSize = GetCollectionSize(config);

            Initialize();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SqlErrorLog"/> class
        /// to use a specific connection string for connecting to the database.
        /// </summary>
        public MongoErrorLog(string connectionString)
        {
            if (connectionString == null)
                throw new ArgumentNullException(nameof(connectionString));

            if (connectionString.Length == 0)
                throw new ArgumentException(null, nameof(connectionString));

            _connectionString = connectionString;

            Initialize();
        }

        static MongoErrorLog()
        {
            BsonSerializer.RegisterSerializer(typeof(NameValueCollection), NameValueCollectionSerializer.Instance);
            BsonClassMap.RegisterClassMap<Error>(cm =>
            {
                cm.MapProperty(c => c.ApplicationName);
                cm.MapProperty(c => c.HostName).SetElementName("host");
                cm.MapProperty(c => c.Type).SetElementName("type");
                cm.MapProperty(c => c.Source).SetElementName("source");
                cm.MapProperty(c => c.Message).SetElementName("message");
                cm.MapProperty(c => c.Detail).SetElementName("detail");
                cm.MapProperty(c => c.User).SetElementName("user");
                cm.MapProperty(c => c.Time).SetElementName("time");
                cm.MapProperty(c => c.StatusCode).SetElementName("statusCode");
                cm.MapProperty(c => c.WebHostHtmlMessage).SetElementName("webHostHtmlMessage");
                cm.MapField("_serverVariables").SetElementName("serverVariables");
                cm.MapField("_queryString").SetElementName("queryString");
                cm.MapField("_form").SetElementName("form");
                cm.MapField("_cookies").SetElementName("cookies");
            });
        }

        private void Initialize()
        {
            lock (Sync)
            {
                var mongoUrl = MongoUrl.Create(_connectionString);
                var mongoClient = new MongoClient(mongoUrl);
                var database = mongoClient.GetDatabase(mongoUrl.DatabaseName);

                if (database.GetCollection<BsonDocument>(_collectionName) == null)
                {
                    var options = new CreateCollectionOptions
                    {
                        Capped = true,
                        MaxSize = _maxSize
                    };
                    if (_maxDocuments != int.MaxValue)
                        options.MaxDocuments = _maxDocuments;
                    database.CreateCollection(_collectionName, options);
                }

                _collection = database.GetCollection<BsonDocument>(_collectionName);

                //create an index on time
                try
                {
                    IndexKeysDefinition<BsonDocument> keys = "{ time: -1 }";
                    _collection.Indexes.CreateOne(
                        new CreateIndexModel<BsonDocument>(
                            keys,
                            new CreateIndexOptions
                            {
                                Background = true,
                                Name = "ix_time"
                            })
                        );
                }
                catch
                {
                    // ignore any error
                }
            }
        }

        /// <summary>
        /// Gets the name of this error log implementation.
        /// </summary>
        public override string Name
        {
            get { return "MongoDB Error Log"; }
        }

        /// <summary>
        /// Gets the connection string used by the log to connect to the database.
        /// </summary>
        public virtual string ConnectionString
        {
            get { return _connectionString; }
        }

        /// <summary>
        /// Logs an error in log for the application.
        /// </summary>
        /// <param name="error"></param>
        public override string Log(Error error)
        {
            if (error == null)
                throw new ArgumentNullException(nameof(error));

            error.ApplicationName = ApplicationName;
            var document = error.ToBsonDocument();

            var id = ObjectId.GenerateNewId();
            document.Add("_id", id);

            _collection!.InsertOne(document);

            return id.ToString();
        }

        /// <summary>
        /// Retrieves a single application error from log given its
        /// identifier, or null if it does not exist.
        /// </summary>
        /// <param name="id"></param>
        public override ErrorLogEntry? GetError(string id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (id.Length == 0) throw new ArgumentException(null, nameof(id));

            var document = _collection.Find(new BsonDocument("_id", new ObjectId(id))).FirstOrDefault();

            if (document == null)
                return null;

            var error = BsonSerializer.Deserialize<Error>(document);

            return new ErrorLogEntry(this, id, error);
        }

        /// <summary>
        /// Retrieves a page of application errors from the log in
        /// descending order of logged time.
        /// </summary>
        /// <param name="pageIndex"></param>
        /// <param name="pageSize"></param>
        /// <param name="errorEntryList"></param>
        public override int GetErrors(int pageIndex, int pageSize, IList errorEntryList)
        {
            if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, null);
            if (pageSize < 0) throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, null);

            // Need to force documents into descending date order
            var sort = Builders<BsonDocument>.Sort.Descending("time");
            var documents = _collection
                .Find(new BsonDocument())
                .Sort(sort)
                .Skip(pageIndex * pageSize)
                .Limit(pageSize)
                .ToList();

            foreach (var document in documents)
            {
                var id = document["_id"].AsObjectId.ToString();
                var error = BsonSerializer.Deserialize<Error>(document);
                error.Time = error.Time.ToLocalTime();
                errorEntryList.Add(new ErrorLogEntry(this, id, error));
            }

            return (int)_collection!.CountDocuments(FilterDefinition<BsonDocument>.Empty);
        }

        public static int GetCollectionLimit(IDictionary config)
        {
            return int.TryParse((string)config["maxDocuments"], out int result) ? result : DefaultMaxDocuments;
        }

        public static int GetCollectionSize(IDictionary config)
        {
            return int.TryParse((string)config["maxSize"], out int result) ? result : DefaultMaxSize;
        }

        public string GetConnectionString(IDictionary config)
        {
            var connectionStringName = config["connectionStringName"].ToString();

            return System.Configuration.ConfigurationManager.ConnectionStrings[connectionStringName].ConnectionString;
        }
    }
}
