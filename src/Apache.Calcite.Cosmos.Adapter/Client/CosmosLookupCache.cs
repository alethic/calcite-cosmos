using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Sql;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Remembers what the lookup join fetched, across executions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One instance per container, owned by the schema, with the freshness policy declared in the
    /// model — see <c>DESIGN.md</c> under <em>The lookup join's caches</em>. Entries are JSON rows
    /// keyed by statement and key rather than built rows, so an entry serves every plan that
    /// renders the same statement; rows are rebuilt per execution, which is the price of sharing.
    /// </para>
    /// <para>
    /// Expiry is the only eviction. A full cache purges what has expired and otherwise declines new
    /// entries — the same fill-to-bound honesty as the within-execution cache, with the time-to-live
    /// providing turnover. The bound counts rows, an absence entry counting as one: absence is the
    /// answer a cache most needs to hold, and it must not be free to hold without limit.
    /// </para>
    /// </remarks>
    public sealed class CosmosLookupCache
    {

        sealed record Entry(IReadOnlyList<JsonElement> Rows, DateTimeOffset ExpiresAt)
        {
            public int Cost => Math.Max(1, Rows.Count);
        }

        readonly object _gate = new();
        readonly Dictionary<(string Statement, object Key), Entry> _entries = new();
        readonly int _maxRows;
        readonly TimeSpan _expireAfterWrite;
        readonly TimeProvider _time;

        int _rows;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="maxRows">The most rows the cache holds, an absence entry counting as one.</param>
        /// <param name="expireAfterWrite">How long an entry answers for after it was written.</param>
        /// <param name="time">The clock, replaceable for tests.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRows"/> or <paramref name="expireAfterWrite"/> is not positive.</exception>
        public CosmosLookupCache(int maxRows, TimeSpan expireAfterWrite, TimeProvider? time = null)
        {
            if (maxRows < 1)
                throw new ArgumentOutOfRangeException(nameof(maxRows));
            if (expireAfterWrite <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(expireAfterWrite));

            _maxRows = maxRows;
            _expireAfterWrite = expireAfterWrite;
            _time = time ?? TimeProvider.System;
        }

        /// <summary>
        /// Gets how many rows are held, absence entries counting as one each.
        /// </summary>
        public int Rows
        {
            get { lock (_gate) return _rows; }
        }

        /// <summary>
        /// Derives the identity under which a statement's answers are remembered.
        /// </summary>
        /// <remarks>
        /// The rendered text plus every non-key parameter, value and type: two plans rendering the
        /// same text differ only in what they bound, and a <c>long</c> and a string that print
        /// alike must not share an entry.
        /// </remarks>
        /// <param name="query">The statement as rendered, before a batch's keys are bound.</param>
        /// <returns>The identity.</returns>
        public static string Statement(CosmosQuery query)
        {
            var identity = new StringBuilder(query.Sql);

            foreach (var parameter in query.Parameters)
                identity.Append('\n').Append(parameter.Name).Append('=')
                    .Append(parameter.Value?.GetType().Name).Append(':').Append(parameter.Value);

            return identity.ToString();
        }

        /// <summary>
        /// Returns what is remembered for a key, where anything is and it has not expired.
        /// </summary>
        /// <param name="statement">The statement identity, from <see cref="Statement"/>.</param>
        /// <param name="key">The normalized join key.</param>
        /// <param name="rows">On a hit, the remembered rows — possibly empty, absence being remembered too.</param>
        /// <returns><c>true</c> on a hit.</returns>
        public bool TryGet(string statement, object key, out IReadOnlyList<JsonElement> rows)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue((statement, key), out var entry))
                {
                    if (entry.ExpiresAt > _time.GetUtcNow())
                    {
                        rows = entry.Rows;
                        return true;
                    }

                    _entries.Remove((statement, key));
                    _rows -= entry.Cost;
                }
            }

            rows = Array.Empty<JsonElement>();
            return false;
        }

        /// <summary>
        /// Remembers a key's rows, where the bound allows.
        /// </summary>
        /// <remarks>
        /// A full cache purges expired entries and tries once more; still full, the answer is not
        /// remembered rather than something else being evicted for it. Nothing here knows which key
        /// is worth keeping, and the time-to-live provides the turnover.
        /// </remarks>
        /// <param name="statement">The statement identity, from <see cref="Statement"/>.</param>
        /// <param name="key">The normalized join key.</param>
        /// <param name="rows">The rows the service answered with, possibly none.</param>
        public void Set(string statement, object key, IReadOnlyList<JsonElement> rows)
        {
            if (rows is null)
                throw new ArgumentNullException(nameof(rows));

            var entry = new Entry(rows, _time.GetUtcNow() + _expireAfterWrite);

            lock (_gate)
            {
                if (_entries.TryGetValue((statement, key), out var existing))
                {
                    _entries.Remove((statement, key));
                    _rows -= existing.Cost;
                }

                if (_rows + entry.Cost > _maxRows)
                    Purge();

                if (_rows + entry.Cost > _maxRows)
                    return;

                _entries[(statement, key)] = entry;
                _rows += entry.Cost;
            }
        }

        /// <summary>
        /// Forgets everything — what a write through the adapter does to its container's cache.
        /// </summary>
        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _rows = 0;
            }
        }

        void Purge()
        {
            var now = _time.GetUtcNow();
            var expired = new List<(string, object)>();

            foreach (var pair in _entries)
                if (pair.Value.ExpiresAt <= now)
                    expired.Add(pair.Key);

            foreach (var key in expired)
            {
                _rows -= _entries[key].Cost;
                _entries.Remove(key);
            }
        }

    }

}
