﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ICSharpCode.NRefactory.CSharp;
using JSIL.Ast;
using Mono.Cecil;

namespace JSIL.Internal {
    public enum EscapingMode {
        MemberIdentifier,
        TypeIdentifier,
        String
    }

    public static class Util {
        public static readonly HashSet<string> ReservedWords = new HashSet<string> {
            "break", "do", "instanceof", "typeof",
            "case", "else", "new", "var",
            "catch", "finally", "return", "void",
            "continue", "for", "switch", "while",
            "debugger", "function", "this", "with",
            "default", "if", "throw", "delete",
            "in", "try", "import", "class", "enum",
            "export", "extends", "super", "let",
            "package", "interface", "implements", "private",
            "protected", "public", "static", "yield",
            "const", "true", "false", "null"
        };

        public static Regex ValidIdentifier = new Regex(
            "$[A-Za-z_$]([A-Za-z_$0-9]*)^", 
            RegexOptions.Compiled | RegexOptions.ExplicitCapture
        );

        public static string GetPathOfAssembly (Assembly assembly) {
            var uri = new Uri(assembly.CodeBase);
            var result = Uri.UnescapeDataString(uri.AbsolutePath);

            if (String.IsNullOrWhiteSpace(result))
                result = assembly.Location;

            result = result.Replace('/', System.IO.Path.DirectorySeparatorChar);

            return result;
        }

        public static string EscapeIdentifier (string identifier, EscapingMode escapingMode = EscapingMode.MemberIdentifier) {
            bool isEscaped = false;
            string result = identifier;

            var sb = new StringBuilder();
            for (int i = 0, l = identifier.Length; i < l; i++) {
                var ch = identifier[i];

                switch (ch) {
                    case '.':
                        if (escapingMode != EscapingMode.MemberIdentifier)
                            sb.Append(".");
                        else {
                            sb.Append("_");
                            isEscaped = true;
                        }
                    break;
                    case '/':
                        if (escapingMode == EscapingMode.MemberIdentifier) {
                            sb.Append("_");
                            isEscaped = true;
                        } else if (escapingMode == EscapingMode.TypeIdentifier) {
                            sb.Append("_");
                            isEscaped = true;
                        } else
                            sb.Append("/");
                    break;
                    case '+':
                        if (escapingMode == EscapingMode.MemberIdentifier) {
                            sb.Append("_");
                            isEscaped = true;
                        } else if (escapingMode == EscapingMode.TypeIdentifier) {
                            sb.Append("_");
                            isEscaped = true;
                        } else
                            sb.Append("+");
                    break;
                    case '`':
                        if (escapingMode != EscapingMode.String) {
                            sb.Append("$b");
                        } else {
                            sb.Append("`");
                        }
                        isEscaped = true;
                    break;
                    case '~':
                        sb.Append("$t");
                        isEscaped = true;
                    break;
                    case ':':
                        sb.Append("$co");
                        isEscaped = true;
                    break;
                    case '<':
                        sb.Append("$l");
                        isEscaped = true;
                    break;
                    case '>':
                        sb.Append("$g");
                        isEscaped = true;
                    break;
                    case '(':
                        sb.Append("$lp");
                        isEscaped = true;
                    break;
                    case ')':
                        sb.Append("$rp");
                        isEscaped = true;
                    break;
                    case '{':
                        sb.Append("$lc");
                        isEscaped = true;
                    break;
                    case '}':
                        sb.Append("$rc");
                        isEscaped = true;
                    break;
                    case '[':
                        sb.Append("$lb");
                        isEscaped = true;
                    break;
                    case ']':
                        sb.Append("$rb");
                        isEscaped = true;
                    break;
                    case '@':
                        sb.Append("$at");
                        isEscaped = true;
                    break;
                    case '-':
                        sb.Append("$da");
                        isEscaped = true;
                    break;
                    case '=':
                        sb.Append("$eq");
                        isEscaped = true;
                    break;
                    case ' ':
                        sb.Append("$sp");
                        isEscaped = true;
                    break;
                    case '?':
                        sb.Append("$qu");
                        isEscaped = true;
                    break;
                    case '!':
                        sb.Append("$ex");
                        isEscaped = true;
                    break;
                    case '*':
                        sb.Append("$as");
                        isEscaped = true;
                    break;
                    case '&':
                        sb.Append("$am");
                        isEscaped = true;
                    break;
                    case ',':
                        sb.Append("$cm");
                        isEscaped = true;
                    break;
                    default:
                        if ((ch <= 32) || (ch >= 127)) {
                            sb.AppendFormat("${0:x}", ch);
                            isEscaped = true;
                        } else
                            sb.Append(ch);
                    break;
                }
            }

            if (isEscaped)
                result = sb.ToString();

            bool isReservedWord = ReservedWords.Contains(result);
            if (isReservedWord)
                result = "$" + result;

            return result;
        }

        public static string EscapeCharacter (char character) {
            switch (character) {
                case '\0':
                    return @"\0";
                case '\'':
                    return @"\'";
                case '\\':
                    return @"\\";
                case '"':
                    return "\\\"";
                case '\t':
                    return @"\t";
                case '\r':
                    return @"\r";
                case '\n':
                    return @"\n";
                default: {
                    if (character > 255)
                        return String.Format(@"\u{0:x4}", (int)character);
                    else
                        return String.Format(@"\x{0:x2}", (int)character);
                }
            }
        }

        public static string EscapeString (string text, char quoteCharacter = '\"') {
            if (text == null)
                return "null";

            var sb = new StringBuilder(text.Length + 16);

            sb.Append(quoteCharacter);

            foreach (var ch in text) {
                if (ch == quoteCharacter)
                    sb.Append(EscapeCharacter(ch));
                else if (ch == '\\')
                    sb.Append(@"\\");
                else if ((ch < ' ') || (ch > 127))
                    sb.Append(EscapeCharacter(ch));
                else
                    sb.Append(ch);
            }

            sb.Append(quoteCharacter);

            return sb.ToString();
        }

        public sealed class ListSkipAdapter<T> : IList<T> {
            public readonly IList<T> List;
            public readonly int Offset;

            public ListSkipAdapter (IList<T> list, int offset) {
                List = list;
                Offset = offset;
            }

            public int IndexOf (T item) {
                throw new NotImplementedException("ListSkipAdapter.IndexOf not implemented");
            }

            public void Insert (int index, T item) {
                List.Insert(index + Offset, item);
            }

            public void RemoveAt (int index) {
                List.RemoveAt(index + Offset);
            }

            public T this[int index] {
                get {
                    return List[index + Offset];
                }
                set {
                    List[index + Offset] = value;
                }
            }

            public void Add (T item) {
                List.Add(item);
            }

            public void Clear () {
                throw new NotImplementedException("ListSkipAdapter.Clear not implemented");
            }

            public bool Contains (T item) {
                throw new NotImplementedException("ListSkipAdapter.Contains not implemented");
            }

            public void CopyTo (T[] array, int arrayIndex) {
                for (int i = 0, c = Count; i < c; i++)
                    array[i + arrayIndex] = List[i + Offset];
            }

            public int Count {
                get { return List.Count - Offset; }
            }

            public bool IsReadOnly {
                get { return List.IsReadOnly; }
            }

            public bool Remove (T item) {
                throw new NotImplementedException("ListSkipAdapter.Remove not implemented");
            }

            public IEnumerator<T> GetEnumerator () {
                return (List as IEnumerable<T>).Skip(Offset).GetEnumerator();
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator () {
                return (List as IEnumerable<T>).Skip(Offset).GetEnumerator();
            }
        }

        public static IList<T> Skip<T> (this IList<T> list, int offset) {
            return new ListSkipAdapter<T>(list, offset);
        }

        public static string Indent (object inner) {
            if (inner == null)
                return "";

            var text = inner.ToString();

            return String.Join(
                Environment.NewLine,
                (from l in text.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                 select "    " + l).ToArray()
            );
        }
    }

    public class ConcurrentHashQueue<TValue> {
        protected readonly ConcurrentDictionary<TValue, bool> Dictionary;
        protected readonly ConcurrentQueue<TValue> Queue;

        public ConcurrentHashQueue (IEqualityComparer<TValue> comparer) {
            Queue = new ConcurrentQueue<TValue>();
            Dictionary = new ConcurrentDictionary<TValue, bool>(comparer);
        }

        public ConcurrentHashQueue (int concurrencyLevel, int capacity, IEqualityComparer<TValue> comparer) {
            Queue = new ConcurrentQueue<TValue>();
            Dictionary = new ConcurrentDictionary<TValue, bool>(concurrencyLevel, capacity, comparer);
        }

        public void Clear () {
            Dictionary.Clear();

            TValue temp;
            while (Queue.Count > 0)
                Queue.TryDequeue(out temp);
        }

        public bool TryEnqueue (TValue value) {
            if (Dictionary.TryAdd(value, false)) {
                Queue.Enqueue(value);
                return true;
            }

            return false;
        }

        public bool TryDequeue (out TValue value) {
            bool temp;

            if (Queue.TryDequeue(out value)) {
                Dictionary.TryRemove(value, out temp);
                return true;
            }

            return false;
        }

        public int Count {
            get {
                return Math.Min(Dictionary.Count, Queue.Count);
            }
        }
    }

    public class ConcurrentCache<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, IDisposable {
        protected class ConstructionState : IDisposable {
            private volatile bool IsDisposed;
            private int WaiterCount = 0, DisposePending = 0;
            private readonly ManualResetEventSlim Signal = new ManualResetEventSlim(false);

            public readonly Thread ConstructingThread = Thread.CurrentThread;

            public bool Wait () {
                if (ConstructingThread == Thread.CurrentThread)
                    throw new InvalidOperationException("Recursive construction of cache entry");

                try {
                    Interlocked.Increment(ref WaiterCount);
                    if (IsDisposed)
                        return true;

                    Signal.Wait();
                    return true;
                } catch (ObjectDisposedException) {
                    return false;
                } finally {
                    var newCount = Interlocked.Decrement(ref WaiterCount);

                    if (newCount <= 0) {

                        if (Interlocked.CompareExchange(ref DisposePending, 0, 1) == 1) {
                            IsDisposed = true;
                            Thread.MemoryBarrier();
                            Signal.Dispose();
                        }
                    }
                }
            }

            public void Set () {
                try {
                    if (!IsDisposed)
                        Signal.Set();
                } catch (ObjectDisposedException) {
                    // Threading is hard and I'm lazy.
                }
            }

            public void Dispose () {
                if (Interlocked.Exchange(ref DisposePending, 1) == 0) {
                    if (WaiterCount <= 0) {
                        DisposePending = 0;
                        IsDisposed = true;
                        Thread.MemoryBarrier();
                        Signal.Dispose();
                    }
                }
            }
        }

        protected readonly ConcurrentDictionary<TKey, TValue> Storage;
        protected readonly ConcurrentDictionary<TKey, ConstructionState> States;

        public ConcurrentCache () {
            Storage = new ConcurrentDictionary<TKey, TValue>();
            States = new ConcurrentDictionary<TKey, ConstructionState>();
        }

        public ConcurrentCache (int concurrencyLevel, int capacity) {
            Storage = new ConcurrentDictionary<TKey, TValue>(concurrencyLevel, capacity);
            States = new ConcurrentDictionary<TKey, ConstructionState>(concurrencyLevel, concurrencyLevel);
        }

        public ConcurrentCache (int concurrencyLevel, int capacity, IEqualityComparer<TKey> comparer) {
            Storage = new ConcurrentDictionary<TKey, TValue>(concurrencyLevel, capacity, comparer);
            States = new ConcurrentDictionary<TKey, ConstructionState>(concurrencyLevel, concurrencyLevel, comparer);
        }

        public int Count {
            get {
                return Storage.Count + States.Count;
            }
        }

        public virtual void Dispose () {
            Clear();
        }

        public void Clear () {
            Storage.Clear();

            foreach (var kvp in States)
                kvp.Value.Dispose();

            States.Clear();
        }

        public IEnumerable<TKey> Keys {
            get {
                return Storage.Keys;
            }
        }

        public bool MightContainKey (TKey key) {
            return Storage.ContainsKey(key) || States.ContainsKey(key);
        }

        public bool ContainsKey (TKey key) {
            return Storage.ContainsKey(key);
        }

        public bool TryGet (TKey key, out TValue result) {
            ConstructionState state;

            while (States.TryGetValue(key, out state)) {
                if (!state.Wait()) {
                    result = default(TValue);
                    return false;
                }
            }

            return Storage.TryGetValue(key, out result);
        }

        public bool TryCreate (TKey key, Func<TValue> creator) {
            ConstructionState state;

            if (Storage.ContainsKey(key))
                return false;

            state = new ConstructionState();
            if (States.TryAdd(key, state)) {
                try {
                    if (Storage.ContainsKey(key))
                        return false;

                    var result = creator();

                    if (!Storage.TryAdd(key, result))
                        throw new InvalidOperationException("Cache entry was created by someone else while construction lock was held");

                    return true;
                } finally {
                    States.TryRemove(key, out state);
                    state.Set();
                    state.Dispose();
                }
            } else {
                state.Dispose();
            }

            return false;
        }

        public TValue GetOrCreate (TKey key, Func<TValue> creator) {
            while (true) {
                ConstructionState state;

                bool waitFailed = false;

                while (States.TryGetValue(key, out state)) {
                    waitFailed = !state.Wait();
                }

                TValue result;
                if (Storage.TryGetValue(key, out result))
                    return result;
                else if (waitFailed)
                    throw new ObjectDisposedException("Cache", "The cache was cleared or disposed.");

                TryCreate(key, creator);
            }
        }

        public bool TryRemove (TKey key) {
            ConstructionState state;

            while (States.TryGetValue(key, out state)) {
                if (!state.Wait())
                    return false;
            }

            TValue temp;
            return Storage.TryRemove(key, out temp);
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator () {
            return Storage.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator () {
            return Storage.GetEnumerator();
        }
    }

    public class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class {

        public bool Equals (T x, T y) {
            return x == y;
        }

        public int GetHashCode (T obj) {
            return obj.GetHashCode();
        }
    }
}
