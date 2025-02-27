using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Corax.Utils;
using Sparrow.Server;
using Voron.Util;

namespace Corax.Indexing;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EntriesModifications
{
    private const int ControlBits = 4;
    private const int ControlMask = 0b1111;

    private const int HasLong = 0b0001;
    private const int HasDouble = 0b0010;

    private const int NeedSorting = 0b0100;
    private const int NeeddUpdates = 0b1000;

    private int _termSize;
    private long _longValue;
    private double _doubleValue;

    public int TermSize
    {
        get => _termSize >> ControlBits;
        set => _termSize = _termSize & ControlMask | value << ControlBits;
    }

    public long? Long
    {
        get
        {
            if ((_termSize & HasLong) != HasLong)
                return null;
            return _longValue;
        }
        set
        {
            if (value.HasValue == false)
            {
                _termSize &= ~HasLong;
            }
            else
            {
                _termSize |= HasLong;
                _longValue = value.Value;
            }
        }
    }

    public double? Double
    {
        get
        {
            if ((_termSize & HasDouble) != HasDouble)
                return null;
            return _doubleValue;
        }
        set
        {
            if (value.HasValue == false)
            {
                _termSize &= ~HasDouble;
            }
            else
            {
                _termSize |= HasDouble;
                _doubleValue = value.Value;
            }
        }
    }


    public NativeList<TermInEntryModification> Additions;
    public NativeList<TermInEntryModification> Removals;
    public NativeList<TermInEntryModification> Updates;

    private bool NeedToSort
    {
        get => (_termSize & NeedSorting) != 0;
        set
        {
            if (value == false)
            {
                _termSize &= ~NeedSorting;
            }
            else
            {
                _termSize |= NeedSorting;
            }
        }
    }

    private bool NeedToUpdate
    {
        get => (_termSize & NeeddUpdates) != 0;
        set
        {
            if (value == false)
            {
                _termSize &= ~NeeddUpdates;
            }
            else
            {
                _termSize |= NeeddUpdates;
            }
        }
    }


#if DEBUG
    private int _hasChangesCallCount = 0;
    private bool _preparationFinished = false;
#endif

    public void Prepare([NotNull] ByteStringContext context)
    {
        AssertHasChangesIsCalledOnlyOnce();

        DeleteAllDuplicates(context);
    }

    public bool HasChanges => Additions.Count + Removals.Count > 0;

    public EntriesModifications(int size)
    {
        TermSize = size;
        //do not initialize native list by default since some of them never will be allocated.
        Additions = new();
        Removals = new();
        Updates = new();
    }

    public void Addition([NotNull] ByteStringContext context, long entryId, int termsPerEntryIndex, short freq)
    {
        if (Additions.HasCapacityFor(1) == false)
            Additions.Grow(context, 1);
        AddToList(context, ref Additions, entryId, termsPerEntryIndex, freq);
    }

    public void Removal([NotNull] ByteStringContext context, long entryId, int termsPerEntryIndex, short freq)
    {
        if (Removals.HasCapacityFor(1) == false)
            Removals.Grow(context, 1);
        AddToList(context, ref Removals, entryId, termsPerEntryIndex, freq);
    }

    private void AddToList([NotNull] ByteStringContext context, ref NativeList<TermInEntryModification> list, long entryId, int termsPerEntryIndex, short freq)
    {
        AssertPreparationIsNotFinished();
        NeedToUpdate = true;
        if (list.Count > 0)
        {
            ref var cur = ref list[list.Count - 1];
            if (cur.EntryId == entryId)
            {
                if (cur.Frequency + freq < short.MaxValue)
                {
                    cur.Frequency += freq;
                }
                else
                {
                    cur.Frequency = short.MaxValue;
                }

                return;
            }

            if (cur.EntryId > entryId)
            {
                NeedToSort = true;
            }
        }

        ref var term = ref list.AddByRefUnsafe();
        term = new TermInEntryModification(entryId, termsPerEntryIndex, freq);
    }

    private void DeleteAllDuplicates([NotNull] ByteStringContext context)
    {
        if (NeedToUpdate == false)
            return;

        NeedToUpdate = false;

        if (NeedToSort)
        {
            Additions.Sort();
            Removals.Sort();
            NeedToSort = false;
        }

        var oldUpdates = Updates.Count;
        int additionPos = 0, removalPos = 0;

        int additionIndex = 0, removalIndex = 0;
        for (; additionIndex < Additions.Count && removalIndex < Removals.Count; ++additionIndex)
        {
            ref var currentAdd = ref Additions[additionIndex];
            ref var currentRemoval = ref Removals[removalIndex];

            //We've to delete exactly same item in additions and removals and delete those.
            //This is made for Set structure.
            if (currentAdd.Equals(currentRemoval))
            {
                Updates.Add(context, currentAdd);
                removalIndex++;
                continue;
            }
            
            // if it is equal, then we have same entry, different freq, so need to remove & add
            // the remove is the old one in this case
            if (currentAdd.EntryId >= currentRemoval.EntryId)
            {
                Removals[removalPos++] = currentRemoval;
                removalIndex++;
                additionIndex--; // so the loop increment will stay the same
                continue;
            }

            Additions[additionPos++] = currentAdd;
        }

        for (; additionIndex < Additions.Count; additionIndex++)
        {
            Additions[additionPos++] = Additions[additionIndex];
        }

        for (; removalIndex < Removals.Count; removalIndex++)
        {
            Removals[removalPos++] = Removals[removalIndex];
        }

        Additions.Shrink(additionPos);
        Removals.Shrink(removalPos);

        Debug.Assert(oldUpdates == Updates.Count || oldUpdates == 0 && Updates.Count > 0,
            "New updates on *second* call here should not be possible");

        ValidateNoDuplicateEntries();
    }
    
    public void GetEncodedAdditionsAndRemovals([NotNull] ByteStringContext context, out long* additions, out long* removals)
    {
#if DEBUG
        if (_preparationFinished)
            throw new InvalidOperationException(
                $"{nameof(GetEncodedAdditionsAndRemovals)} should be called only once. This is a bug. It was called via: {Environment.NewLine}" +
                Environment.StackTrace);
        _preparationFinished = true;
#endif
        DeleteAllDuplicates(context);

        // repurposing the memory
        Debug.Assert(sizeof(TermInEntryModification) >= sizeof(long));
        additions = (long*)Additions.RawItems;
        for (int i = 0; i < Additions.Count; i++)
        {
            ref var cur = ref Additions[i];
            additions[i] = EntryIdEncodings.Encode(cur.EntryId, cur.Frequency, TermIdMask.Single);
        }

        removals = (long*)Removals.RawItems;
        for (int i = 0; i < Removals.Count; i++)
        {
            ref var cur = ref Removals[i];
            // Here we use a trick, we want to avoid a 3 way merge, so we use the last bit as indication that this is a
            // value that needs to be removed, after the sorting, we can scan, find the matching removal & addition and skip both
            removals[i] = EntryIdEncodings.Encode(cur.EntryId, cur.Frequency, (TermIdMask)1);
        }
    }

    [Conditional("DEBUG")]
    private void ValidateNoDuplicateEntries()
    {
        var removals = Removals;
        var additions = Additions;
        foreach (var add in additions.ToSpan())
        {
            if (removals.ToSpan().IndexOf(add) >= 0)
                throw new InvalidOperationException("Found duplicate addition & removal item during indexing: " + add);
        }

        foreach (var removal in removals.ToSpan())
        {
            if (additions.ToSpan().IndexOf(removal) >= 0)
                throw new InvalidOperationException("Found duplicate addition & removal item during indexing: " + removal);
        }
    }

    [Conditional("DEBUG")]
    public void AssertPreparationIsNotFinished()
    {
#if DEBUG
        if (_preparationFinished)
            throw new InvalidOperationException("Tried to Add/Remove but data is already encoded.");
#endif
    }

    [Conditional("DEBUG")]
    private void AssertHasChangesIsCalledOnlyOnce()
    {
#if DEBUG
        _hasChangesCallCount++;
        if (_hasChangesCallCount > 1)
            throw new InvalidOperationException($"{nameof(Prepare)} should be called only once.");
#endif
    }

#if DEBUG

    public string DebugViewer()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{nameof(_preparationFinished)}: {_preparationFinished}");
        sb.AppendLine($"{nameof(HasDouble)}: {HasDouble}");
        sb.AppendLine($"{nameof(HasChanges)}: {HasChanges}");
        sb.AppendLine($"{nameof(HasLong)}: {HasLong}");
        sb.AppendLine($"{nameof(_hasChangesCallCount)}: {_hasChangesCallCount}");
        sb.AppendLine($"Addition Count: {Additions.Count}");
        foreach (var add in Additions.ToSpan())
            sb.AppendLine($"+{add.EntryId}|{add.Frequency}");
        sb.AppendLine($"_____________________________");
        sb.AppendLine($"Removals Count: {Removals.Count}");
        foreach (var add in Removals.ToSpan())
            sb.AppendLine($"+{add.EntryId}|{add.Frequency}");

        return sb.ToString();
    }
#endif

    public void Dispose([NotNull] ByteStringContext context)
    {
        Additions.Dispose(context);
        Removals.Dispose(context);
        Updates.Dispose(context);
    }
}
