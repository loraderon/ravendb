using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Corax.Mappings;
using Corax.Queries;
using Voron;
using Voron.Data.Lookups;
using static Voron.Data.CompactTrees.CompactTree;
using Range = Corax.Queries.Range;

namespace Corax;

public partial class IndexSearcher
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch BetweenQuery<TValue>(FieldMetadata field, TValue low, TValue high, UnaryMatchOperation leftSide = UnaryMatchOperation.GreaterThanOrEqual, UnaryMatchOperation rightSide = UnaryMatchOperation.LessThanOrEqual, bool isNegated = false, bool forward = true, bool streamingEnabled = false) {
        if (typeof(TValue) == typeof(long))
        {
            return (leftSide, rightSide) switch
            {
                // (x, y)
                (UnaryMatchOperation.GreaterThan, UnaryMatchOperation.LessThan) => RangeBuilder<Range.Exclusive, Range.Exclusive>(field, (long)(object)low, (long)(object)high, isNegated, forward, streamingEnabled),

                //<x, y)
                (UnaryMatchOperation.GreaterThanOrEqual, UnaryMatchOperation.LessThan) => RangeBuilder<Range.Inclusive, Range.Exclusive>(field, (long)(object)low, (long)(object)high, isNegated, forward, streamingEnabled),

                //<x, y>
                (UnaryMatchOperation.GreaterThanOrEqual, UnaryMatchOperation.LessThanOrEqual) => RangeBuilder<Range.Inclusive, Range.Inclusive>(field, (long)(object)low, (long)(object)high, isNegated, forward, streamingEnabled),

                //(x, y>
                (UnaryMatchOperation.GreaterThan, UnaryMatchOperation.LessThanOrEqual) => RangeBuilder<Range.Exclusive, Range.Inclusive>(field, (long)(object)low, (long)(object)high, isNegated, forward, streamingEnabled),
                _ => throw new ArgumentOutOfRangeException($"Unknown operation at {nameof(BetweenQuery)}.")
            };
        }

        if (typeof(TValue) == typeof(double))
        {
            return (leftSide, rightSide) switch
            {
                // (x, y)
                (UnaryMatchOperation.GreaterThan, UnaryMatchOperation.LessThan) => RangeBuilder<Range.Exclusive, Range.Exclusive>(field, (double)(object)low, (double)(object)high, isNegated, forward, streamingEnabled),

                //<x, y)
                (UnaryMatchOperation.GreaterThanOrEqual, UnaryMatchOperation.LessThan) => RangeBuilder<Range.Inclusive, Range.Exclusive>(field, (double)(object)low, (double)(object)high, isNegated, forward, streamingEnabled),

                //<x, y>
                (UnaryMatchOperation.GreaterThanOrEqual, UnaryMatchOperation.LessThanOrEqual) => RangeBuilder<Range.Inclusive, Range.Inclusive>(field, (double)(object)low, (double)(object)high, isNegated, forward, streamingEnabled),

                //(x, y>
                (UnaryMatchOperation.GreaterThan, UnaryMatchOperation.LessThanOrEqual) => RangeBuilder<Range.Exclusive, Range.Inclusive>(field, (double)(object)low, (double)(object)high, isNegated, forward, streamingEnabled),
                _ => throw new ArgumentOutOfRangeException($"Unknown operation at {nameof(BetweenQuery)}.")

            };
        }

        if (typeof(TValue) == typeof(string))
        {
            var leftValue = EncodeAndApplyAnalyzer(field, (string)(object)low);
            var rightValue = EncodeAndApplyAnalyzer(field, (string)(object)high);

            return (leftSide, rightSide) switch
            {
                // (x, y)
                (UnaryMatchOperation.GreaterThan, UnaryMatchOperation.LessThan) => RangeBuilder<Range.Exclusive, Range.Exclusive>(field,
                    leftValue, rightValue, isNegated, forward, streamingEnabled),

                //<x, y)
                (UnaryMatchOperation.GreaterThanOrEqual, UnaryMatchOperation.LessThan) => RangeBuilder<Range.Inclusive, Range.Exclusive>(field,
                    leftValue, rightValue, isNegated, forward, streamingEnabled),

                //<x, y>
                (UnaryMatchOperation.GreaterThanOrEqual, UnaryMatchOperation.LessThanOrEqual) => RangeBuilder<Range.Inclusive, Range.Inclusive>(
                    field, leftValue, rightValue, isNegated, forward, streamingEnabled),

                //(x, y>
                (UnaryMatchOperation.GreaterThan, UnaryMatchOperation.LessThanOrEqual) => RangeBuilder<Range.Exclusive, Range.Inclusive>(field,
                    leftValue, rightValue, isNegated, forward, streamingEnabled),

                _ => throw new ArgumentOutOfRangeException($"Unknown operation at {nameof(BetweenQuery)}.")
            };
        }

        throw new ArgumentException($"{typeof(TValue)} is not supported in {nameof(BetweenQuery)}");
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch GreaterThanQuery<TValue>(FieldMetadata field, TValue value, bool isNegated = false, bool forward = true, bool streamingEnabled = false)
    {
        return GreatBuilder<Range.Exclusive, Range.Inclusive, TValue>(field, value, isNegated, forward, streamingEnabled);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch GreatThanOrEqualsQuery<TValue>(FieldMetadata field, TValue value, bool isNegated = false, bool forward = true, bool streamingEnabled = false)
    {
        return GreatBuilder<Range.Inclusive, Range.Inclusive, TValue>(field, value, isNegated, forward, streamingEnabled);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MultiTermMatch GreatBuilder<TLeftRange, TRightRange, TValue>(FieldMetadata field, TValue value, bool isNegated = false, bool forward = true, bool streamingEnabled = false)
        where TLeftRange : struct, Range.Marker
        where TRightRange : struct, Range.Marker
    {
        
        if (typeof(TValue) == typeof(long))
        {
            
            return RangeBuilder<TLeftRange, TRightRange>(field, (long)(object)value, long.MaxValue, isNegated, forward, streamingEnabled);
        }

        if (typeof(TValue) == typeof(double))
            return RangeBuilder<TLeftRange, TRightRange>(field, (double)(object)value, double.MaxValue, isNegated, forward, streamingEnabled);
        if (typeof(TValue) == typeof(string))
        {
            var sliceValue = EncodeAndApplyAnalyzer(field, (string)(object)value);
            return RangeBuilder<TLeftRange, TRightRange>(field, sliceValue, Slices.AfterAllKeys,  isNegated, forward, streamingEnabled);
        }

        throw new ArgumentException("Range queries are supporting strings, longs or doubles only");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch LessThanOrEqualsQuery<TValue>(FieldMetadata field, TValue value, bool isNegated = false, bool forward = true, bool streamingEnabled = false)
        => LessBuilder<Range.Inclusive, Range.Inclusive, TValue>(field, value, isNegated, forward, streamingEnabled);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch LessThanQuery<TValue>(FieldMetadata field, TValue value, bool isNegated = false, bool forward = true, bool streamingEnabled = false)
        => LessBuilder<Range.Inclusive, Range.Exclusive, TValue>(field, value, isNegated, forward, streamingEnabled);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MultiTermMatch LessBuilder<TLeftRange, TRightRange, TValue>(FieldMetadata field, TValue value,
        bool isNegated, bool forward, bool streamingEnabled)
        where TLeftRange : struct, Range.Marker
        where TRightRange : struct, Range.Marker
    {
        if (typeof(TValue) == typeof(long))
            return RangeBuilder<TLeftRange, TRightRange>(field, long.MinValue, (long)(object)value, isNegated, forward, streamingEnabled);

        if (typeof(TValue) == typeof(double))
            return RangeBuilder<TLeftRange, TRightRange>(field, double.MinValue, (double)(object)value, isNegated, forward, streamingEnabled);
        
        if (typeof(TValue) == typeof(string))
        {
            var sliceValue = EncodeAndApplyAnalyzer(field, (string)(object)value);
            return RangeBuilder<TLeftRange, TRightRange>(field, Slices.BeforeAllKeys, sliceValue, isNegated, forward, streamingEnabled);
        }

        throw new ArgumentException("Range queries are supporting strings, longs or doubles only");
    }
    
    private MultiTermMatch RangeBuilder<TLow, THigh>(FieldMetadata field, Slice low, Slice high, bool isNegated, bool forward, bool streamingEnabled)
        where TLow : struct, Range.Marker
        where THigh : struct, Range.Marker
    {
        var terms = _fieldsTree?.CompactTreeFor(field.FieldName);
        if (terms == null)
            return MultiTermMatch.CreateEmpty(_transaction.Allocator);

        return forward == true
            ? MultiTermMatch.Create(
                new MultiTermMatch<TermRangeProvider<Lookup<CompactKeyLookup>.ForwardIterator, TLow, THigh>>(this,
                    field, _transaction.Allocator,
                    new TermRangeProvider<Lookup<CompactKeyLookup>.ForwardIterator, TLow, THigh>(this, terms, field, low, high), streamingEnabled))
            : MultiTermMatch.Create(
                new MultiTermMatch<TermRangeProvider<Lookup<CompactKeyLookup>.BackwardIterator, TLow, THigh>>(this,
                    field, _transaction.Allocator,
                    new TermRangeProvider<Lookup<CompactKeyLookup>.BackwardIterator, TLow, THigh>(this, terms, field, low, high), streamingEnabled));
    }

    private MultiTermMatch RangeBuilder<TLow, THigh>(FieldMetadata field, long low, long high, bool isNegated, bool forward, bool streamingEnabled)
        where TLow : struct, Range.Marker
        where THigh : struct, Range.Marker
    {
        var terms = _fieldsTree?.CompactTreeFor(field.FieldName);
        if (terms == null)
            return MultiTermMatch.CreateEmpty(_transaction.Allocator);

        field = field.GetNumericFieldMetadata<long>(Allocator);
        var set = _fieldsTree?.LookupFor<Int64LookupKey>(field.FieldName);

        return forward 
            ? MultiTermMatch.Create(new MultiTermMatch<TermNumericRangeProvider<Lookup<Int64LookupKey>.ForwardIterator, TLow, THigh, Int64LookupKey>>(this, field, _transaction.Allocator, new TermNumericRangeProvider<Lookup<Int64LookupKey>.ForwardIterator, TLow, THigh, Int64LookupKey>(this, set, field, low, high), streamingEnabled)) 
            : MultiTermMatch.Create(new MultiTermMatch<TermNumericRangeProvider<Lookup<Int64LookupKey>.BackwardIterator, TLow, THigh, Int64LookupKey>>(this, field, _transaction.Allocator, new TermNumericRangeProvider<Lookup<Int64LookupKey>.BackwardIterator, TLow, THigh, Int64LookupKey>(this, set, field, low, high), streamingEnabled));
    }

    private MultiTermMatch RangeBuilder<TLow, THigh>(FieldMetadata field, double low, double high, bool isNegated, bool forward, bool streamingEnabled)
        where TLow : struct, Range.Marker
        where THigh : struct, Range.Marker
    {
        var terms = _fieldsTree?.CompactTreeFor(field.FieldName);
        if (terms == null)
            return MultiTermMatch.CreateEmpty(_transaction.Allocator);
 
        
        field = field.GetNumericFieldMetadata<double>(Allocator);
        var set = _fieldsTree?.LookupFor<DoubleLookupKey>(field.FieldName); 
        return forward
            ? MultiTermMatch.Create(new MultiTermMatch<TermNumericRangeProvider<Lookup<DoubleLookupKey>.ForwardIterator, TLow, THigh, DoubleLookupKey>>(this, field,
                _transaction.Allocator, new TermNumericRangeProvider<Lookup<DoubleLookupKey>.ForwardIterator, TLow, THigh, DoubleLookupKey>(this, set, field, low, high), streamingEnabled))
            : MultiTermMatch.Create(new MultiTermMatch<TermNumericRangeProvider<Lookup<DoubleLookupKey>.BackwardIterator, TLow, THigh, DoubleLookupKey>>(this, field,
                _transaction.Allocator,
                new TermNumericRangeProvider<Lookup<DoubleLookupKey>.BackwardIterator, TLow, THigh, DoubleLookupKey>(this, set, field, low, high), streamingEnabled));
    }
}
