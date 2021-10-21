# RecordEmitter

POC for emitting `record struct`-like types (for use in PowerShell)

## What?

C# 10.0 (currently in preview) introduces value-type records (`record struct`) as a native part of the language syntax. 

In reality, `record struct` is just a macro for generating structs with a constructor that takes all members as arguments and provides safe implementations of `IEquatable<T>.Equals()`, `object.ToString()` and a tuple deconstructor - all of which are type traits we can already emit using `System.Reflection.Emit`. 

This POC intends to showcase how an identical type compilation routine could be introduced for emission of `record struct`-like types in PowerShell.

## How does it work?

Much like the existing type emitter in PowerShell, RecordEmitter does the following:  
 1. Create dynamic assembly at runtime (currently it caches a single static assembly for reuse = name collisions might occur)
 2. Define a new struct type (eg. a subtype of `System.ValueType`)
 3. Generate property bindings for all members
 4. Generate IL for default get/set routines for each property 
 5. Generate safe GetHashCode() + object.Equals() overrides based on available members
 6. Generate `ToString()` override + `PrintMembers()` helper function, to predictably construct meaningful string representations
 7. Generate a single public constructor based on member layout
 8. Generate tuple deconstructor with signature based on member layout

The following features are currently incomplete or not implemented at all:
 - Explicit implementation of `IEquatable<T>` (the emitter currently generates a safe/correct `Equals(T, T)` implementation, but the type doesn't declare `IEquatable<t>` as an implemented interface)
 - `ToString()` override - the heavy lifting is done in `PrintMembers()`, but no method override currently exists for `base.ToString()`
 - `object.Equals(object obj)` override is buggy - invoking the type-specific Equals implementation works, but `object.Equals(obj)` (which is the entry point when using `$record -eq $record2` for example) results in invalid IL generation at runtime (likely a problem with argument alignment)
 - Deconstructor - `record struct` results in the generation of a `void Deconstruct(out <member1Type> member1, out <member2Type> member2, ..., out <memberNType> memberN)` method, not prioritized because of PowerShell's limited use of tuple deconstruction
