using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace RecordEmitter {

    public class RETyper
    {
        private static AssemblyBuilder s_asm;
        private static ModuleBuilder s_mod;

        private RETyper()
        {
        }

        static RETyper()
        {
            var asmGuid = Guid.NewGuid();
            var asmName = new AssemblyName($"_RETyperAssembly__{asmGuid}");
            s_asm = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.RunAndCollect);
            s_mod = s_asm.DefineDynamicModule($"_RETyperModule_<{Guid.NewGuid()}>_{asmGuid}");
            s_object_Equals = typeof(Object).GetMethod("Equals", BindingFlags.Public | BindingFlags.Instance);
            s_object_ToString = typeof(Object).GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance);

            s_comparerCache = new Dictionary<Type, Type>();
        }

        public struct RecordMember
        {
            public Type Type { get; }
            public string Name { get; }
            public PropertyInfo? Member { get; internal set; }
            public FieldInfo? BackingField { get; internal set; }

            public RecordMember(Type type, string name)
                : this(type, name, null, null)
            {
            }

            internal RecordMember(Type type, string name, PropertyInfo? member = null, FieldInfo? backingField = null)
            {
                this.Type = type;
                this.Name = name;
                this.Member = member;
                this.BackingField = backingField;
            }
        }

        public static Type CreateRecordStruct(string name, IEnumerable<RecordMember> members)
            => CreateRecordType(name, members, isStruct: true);

        private static RecordMember[] DefineMembers(TypeBuilder type, RecordMember[] members)
        {
            var definedMembers = new RecordMember[members.Length];

            // create properties + backing fields for each struct member
            var idx = 0;
            foreach(var recordMember in members)
            {
                var memberName = recordMember.Name;
                var memberType = recordMember.Type;
                var backingField = type.DefineField($"<{memberName}>RETyper__BackingField", memberType, FieldAttributes.Private);
                var member = type.DefineProperty(memberName, PropertyAttributes.None, memberType, Type.EmptyTypes);

                var getSetAttributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
                
                // get-method: load backing field value and return;
                var getMethod = type.DefineMethod($"get_{memberName}", getSetAttributes, memberType, Type.EmptyTypes);
                var getILGen = getMethod.GetILGenerator();

                getILGen.Emit(OpCodes.Ldarg_0);
                getILGen.Emit(OpCodes.Ldfld, backingField);
                getILGen.Emit(OpCodes.Ret);
                member.SetGetMethod(getMethod);

                // set-method: load first param arg, store in backing field;
                var setMethod = type.DefineMethod($"set_{memberName}", getSetAttributes, memberType, Type.EmptyTypes);
                var setILGen = setMethod.GetILGenerator();

                setILGen.Emit(OpCodes.Ldarg_0);
                setILGen.Emit(OpCodes.Ldarg_1);
                setILGen.Emit(OpCodes.Stfld, backingField);
                setILGen.Emit(OpCodes.Ret);
                member.SetSetMethod(setMethod);
                definedMembers[idx++] = new RecordMember(recordMember.Type, recordMember.Name){ Member = member, BackingField = backingField };
            }

            return definedMembers;
        }

        internal static Type CreateRecordType(string name, IEnumerable<RecordMember> members, bool isStruct = false)
        {
            var typeAttributes = TypeAttributes.Public | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit;
            
            if(isStruct)
                typeAttributes |= TypeAttributes.SequentialLayout | TypeAttributes.Sealed;
            else
                typeAttributes |= TypeAttributes.AutoLayout;

            var recordType = s_mod.DefineType(name, typeAttributes);
            var definedMembers = DefineMembers(recordType, members.ToArray());
            var recordCtor = DefineConstructor(recordType, definedMembers);
            // TODO: DefineDeconstructor(recordType, definedMembers);
            DefineEqualityOverrides(recordType, definedMembers);
            DefineStringOverloads(recordType, definedMembers);

            return recordType.CreateType() ?? throw new Exception("Failed to create record type");
        }

        private static object DefineConstructor(TypeBuilder recordType, RecordMember[] definedMembers)
        {
            var ctorAttributes = 
                MethodAttributes.Public |
                MethodAttributes.HideBySig |
                MethodAttributes.SpecialName |
                MethodAttributes.RTSpecialName;

            var ctor = recordType.DefineConstructor(ctorAttributes, CallingConventions.HasThis, definedMembers.Select(m=>m.Type).ToArray());
            var ctorParams = definedMembers.Select((m, i) => ctor.DefineParameter(i+1, ParameterAttributes.None, m.Name)).ToArray();
            var ctorILGen = ctor.GetILGenerator();
            for(var idx = 1; idx <= definedMembers.Length; idx++)
            {
                ctorILGen.Emit(OpCodes.Ldarg_0);
                ctorILGen.Emit(OpCodes.Ldarg_S, (byte)idx);
                ctorILGen.Emit(OpCodes.Stfld, definedMembers[idx - 1].BackingField ?? throw new Exception("Unable to obtain backing field"));
            }

            ctorILGen.Emit(OpCodes.Ret);

            return ctor;
        }

        private static void DefineStringOverloads(TypeBuilder recordType, RecordMember[] definedMembers)
        {
            var strBuilderAppendString = typeof(StringBuilder).GetMethods(BindingFlags.Instance | BindingFlags.Public).First(m => m.Name == "Append" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            var strBuilderAppendObject = typeof(StringBuilder).GetMethods(BindingFlags.Instance | BindingFlags.Public).First(m => m.Name == "Append" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(object));

            // PrintMembers()
            var printMembers = recordType.DefineMethod("PrintMembers", MethodAttributes.Private | MethodAttributes.HideBySig, typeof(bool), new[]{typeof(StringBuilder)});
            printMembers.DefineParameter(1, ParameterAttributes.None, "stringBuilder");

            var printMembersILGen = printMembers.GetILGenerator();
            var printMembersLocal = printMembersILGen.DeclareLocal(typeof(int));

            var idx = 0;
            foreach(var member in definedMembers)
            {
                var getter = member.Member?.GetGetMethod() ?? throw new Exception($"Unable to obtain getter method for member {member.Name}");
                printMembersILGen.Emit(OpCodes.Ldarg_1);
                printMembersILGen.Emit(OpCodes.Ldstr, $"{(idx == 0 ? "" : ", ")}{member.Name} = ");
                printMembersILGen.EmitCall(OpCodes.Callvirt, strBuilderAppendString, null);
                printMembersILGen.Emit(OpCodes.Pop);

                printMembersILGen.Emit(OpCodes.Ldarg_1);
                printMembersILGen.Emit(OpCodes.Ldarg_0);
                printMembersILGen.EmitCall(OpCodes.Call, getter, null);
                printMembersILGen.Emit(OpCodes.Stloc_0);
                printMembersILGen.Emit(OpCodes.Ldloca_S);
                
                if(member.Type.IsValueType)
                {
                    var objectToString = s_object_ToString ?? throw new Exception($"Unable to obtain method info for Object.ToString()");
                    printMembersILGen.Emit(OpCodes.Constrained, member.Type);
                    printMembersILGen.EmitCall(OpCodes.Callvirt, objectToString, null);
                    printMembersILGen.EmitCall(OpCodes.Callvirt, strBuilderAppendString, null);
                }
                else
                {
                    printMembersILGen.EmitCall(OpCodes.Callvirt, strBuilderAppendObject, null);
                }

                printMembersILGen.Emit(OpCodes.Pop);
            }

            printMembersILGen.Emit(OpCodes.Ldc_I4_0);
            printMembersILGen.Emit(OpCodes.Ret);
        }

        private static void DefineDeconstructor(TypeBuilder recordType, RecordMember[] definedMembers)
        {
            throw new NotImplementedException();
        }

        private static MethodInfo? s_object_Equals;
        private static MethodInfo? s_object_ToString;
        private static Dictionary<Type, Type> s_comparerCache;

        private static Type GetCachedComparer(Type targetType)
        {
            Type? comparerType;
            if(!s_comparerCache.TryGetValue(targetType, out comparerType))
            {
                try{
                    var newComparerType = typeof(EqualityComparer<>).MakeGenericType(targetType);
                    s_comparerCache.Add(targetType, newComparerType);
                    comparerType = newComparerType;
                }
                catch{
                    comparerType = typeof(EqualityComparer<object>);
                }
            }

            return comparerType;
        }

        internal struct MemberComparerInfo
        {
            internal MemberComparerInfo(Type memberType, Type comparerType, MethodInfo defaultGetter, MethodInfo equalsMethod)
            {
                MemberType = memberType;
                ComparerType = comparerType;
                DefaultGetter = defaultGetter;
                EqualsMethod = equalsMethod;
            }

            public Type MemberType { get; }
            public Type ComparerType { get; }
            public MethodInfo DefaultGetter { get; }
            public MethodInfo EqualsMethod { get; }
        }

        private static MemberComparerInfo GetMemberComparerInfo(RecordMember member)
        {
            var comparer = GetMemberComparer(member);
            var comparerMethods = comparer.GetMethods();
            var defaultGetter = comparerMethods.First(m => m.IsStatic && m.Name == "get_Default");
            var equalsMethod = comparerMethods.First(m => !m.IsStatic && m.Name == "Equals" && m.GetParameters().Length == 2);
            
            return new MemberComparerInfo(member.Type, comparer, defaultGetter, equalsMethod);
        }

        private static Type GetMemberComparer(RecordMember member)
        {
            return GetCachedComparer(member.Type);
        }

        internal static void DefineEqualityOverrides(TypeBuilder type, RecordMember[] members)
        {
            // IEquatable.Equals()
            var iequalsMethod = type.DefineMethod("Equals",
                                                MethodAttributes.Public    |
                                                MethodAttributes.Final     |
                                                MethodAttributes.HideBySig |
                                                MethodAttributes.NewSlot   |
                                                MethodAttributes.Virtual, 
                                                typeof(bool),
                                                new []{ type });
            var iequalsILGen = iequalsMethod.GetILGenerator();
            var iequalsLabelLast = iequalsILGen.DefineLabel();
            var iequalsLabelRet = iequalsILGen.DefineLabel();

            int idx = 0;
            foreach(var member in members)
            {
                var backingField = member.BackingField ?? throw new Exception($"Unable to obtain backing FieldInfo for {member.Name}");
                var comparerInfo = GetMemberComparerInfo(member);
                iequalsILGen.EmitCall(OpCodes.Call, comparerInfo.DefaultGetter, null);
                iequalsILGen.Emit(OpCodes.Ldarg_0);
                iequalsILGen.Emit(OpCodes.Ldfld, backingField);
                iequalsILGen.Emit(OpCodes.Ldarg_1);
                iequalsILGen.Emit(OpCodes.Ldfld, backingField);
                iequalsILGen.EmitCall(OpCodes.Callvirt, comparerInfo.EqualsMethod, null);
                
                if(++idx == members.Length)
                {
                    iequalsILGen.Emit(OpCodes.Br_S, iequalsLabelRet);
                }
                else
                {
                    iequalsILGen.Emit(OpCodes.Brfalse_S, iequalsLabelLast);
                }
            }

            iequalsILGen.MarkLabel(iequalsLabelLast);
            iequalsILGen.Emit(OpCodes.Ldc_I4_0);
            iequalsILGen.MarkLabel(iequalsLabelRet);
            iequalsILGen.Emit(OpCodes.Ret);

            // override object.Equals()
            var equalsMethod = type.DefineMethod("Equals", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual, typeof(bool), new[]{ typeof(object) });
            equalsMethod.DefineParameter(1, ParameterAttributes.None, "obj");
            var equalsILGen = equalsMethod.GetILGenerator();
            var equalsLabelNotType = equalsILGen.DefineLabel();
            equalsILGen.Emit(OpCodes.Ldarg_1);
            equalsILGen.Emit(OpCodes.Isinst, type);
            equalsILGen.Emit(OpCodes.Brfalse_S, equalsLabelNotType);

            equalsILGen.Emit(OpCodes.Ldarg_0);
            equalsILGen.Emit(OpCodes.Ldarg_1);
            equalsILGen.Emit(OpCodes.Unbox_Any, type);
            equalsILGen.EmitCall(OpCodes.Call, iequalsMethod, null);

            equalsILGen.MarkLabel(equalsLabelNotType);
            equalsILGen.Emit(OpCodes.Ldc_I4_0);
            equalsILGen.Emit(OpCodes.Ret);

            var objectEquals = s_object_Equals ?? throw new Exception("Unable to obtain MethodInfo for Object.Equals(a, b)");
            type.DefineMethodOverride(equalsMethod, s_object_Equals);

            // operators
            var operatorAttributes = 
                MethodAttributes.Public
              | MethodAttributes.Static
              | MethodAttributes.HideBySig
              | MethodAttributes.SpecialName;

            // op_Equality (==)
            var isEqualOperatorImpl = type.DefineMethod("op_Equality", operatorAttributes, typeof(bool), new []{ type, type });
            var isEqualLhs = isEqualOperatorImpl.DefineParameter(1, ParameterAttributes.None, "a");
            var isEqualRhs = isEqualOperatorImpl.DefineParameter(2, ParameterAttributes.None, "b");

            var isEqualILGen = isEqualOperatorImpl.GetILGenerator();
            isEqualILGen.Emit(OpCodes.Ldarga_S, (byte)0x1);
            isEqualILGen.Emit(OpCodes.Ldarg_1);
            isEqualILGen.EmitCall(OpCodes.Call, iequalsMethod, null);
            isEqualILGen.Emit(OpCodes.Ret);

            // op_Inequality (!=)
            var isNotEqualOperatorImpl = type.DefineMethod("op_Inequality", operatorAttributes, typeof(bool), new []{ type, type });
            var isNotEqualLhs = isNotEqualOperatorImpl.DefineParameter(1, ParameterAttributes.None, "a");
            var isNotEqualRhs = isNotEqualOperatorImpl.DefineParameter(2, ParameterAttributes.None, "b");

            var isNotEqualILGen = isNotEqualOperatorImpl.GetILGenerator();
            isNotEqualILGen.Emit(OpCodes.Ldarg_0);
            isNotEqualILGen.Emit(OpCodes.Ldarg_1);
            isNotEqualILGen.EmitCall(OpCodes.Call, isEqualOperatorImpl, null);
            isNotEqualILGen.Emit(OpCodes.Ldc_I4_0);
            isNotEqualILGen.Emit(OpCodes.Ceq);
            isNotEqualILGen.Emit(OpCodes.Ret);
        }
    }

    // Reference struct - compiles to the same as `record struct PersonRecordStructManual(int id, string name, string name2, int[] arr);`
    public struct PersonRecordStructManual : IEquatable<PersonRecordStructManual>
    {
        public int id { get; }
        public string name { get; }
        public string name2 { get; }
        public int[] arr { get; }

        public PersonRecordStructManual(int id, string name, string name2, int[] arr)
        {
            this.id = id;
            this.name = name;
            this.name2 = name2;
            this.arr = arr;
        }

        public void Deconstruct(out int id, out string name, out string name2, out int[] arr)
        {
            id = this.id;
            name = this.name;
            name2 = this.name;
            arr = this.arr;
        }

        public override bool Equals(object? obj)
        {
            return obj is PersonRecordStructManual && Equals((PersonRecordStructManual)obj);
        }

        public bool Equals(PersonRecordStructManual other)
        {
            return
                EqualityComparer<int>.Default.Equals(id, other.id)
             && EqualityComparer<string>.Default.Equals(name, other.name)
             && EqualityComparer<string>.Default.Equals(name2, other.name2)
             && EqualityComparer<int[]>.Default.Equals(arr, other.arr);
        }

        public static bool operator ==(PersonRecordStructManual a, PersonRecordStructManual b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(PersonRecordStructManual a, PersonRecordStructManual b)
        {
            return !(a == b);
        }

        private bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder
                .AppendFormat("{0} = {1}", nameof(id), id)
                .Append(", ")
                .AppendFormat("{0} = {1}", nameof(name), name)
                .Append(", ")
                .AppendFormat("{0} = {1}", nameof(name2), name2)
                .Append(", ")
                .AppendFormat("{0} = {1}", nameof(arr), arr);
            
            return true;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(nameof(PersonRecordStructManual));
            sb.Append(" { ");
            if(PrintMembers(sb))
            {
                sb.Append(' ');
            }
            sb.Append('}');
            return sb.ToString();
        }

        public override int GetHashCode()
        {
            var hashCode = 0;
            
            hashCode += EqualityComparer<int>.Default.GetHashCode(id);
            hashCode *= -1521134295;
            hashCode += EqualityComparer<string>.Default.GetHashCode(name);
            hashCode *= -1521134295; 
            hashCode += EqualityComparer<string>.Default.GetHashCode(name2);
            hashCode *= -1521134295;
            hashCode += EqualityComparer<int[]>.Default.GetHashCode(arr);
            return hashCode;
        }
    }
}