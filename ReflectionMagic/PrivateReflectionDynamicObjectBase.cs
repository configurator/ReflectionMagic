﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;

namespace ReflectionMagic {
    public abstract class PrivateReflectionDynamicObjectBase : DynamicObject {
        // We need to virtualize this so we use a different cache for instance and static props
        internal abstract IDictionary<Type, IDictionary<string, IProperty>> PropertiesOnType { get; }

        protected abstract Type TargetType { get; }
        protected abstract object Instance { get; }
        protected abstract BindingFlags BindingFlags { get; }

        public abstract object RealObject { get; }

        public override bool TryGetMember(GetMemberBinder binder, out object result) {
            IProperty prop = GetProperty(binder.Name);

            // Get the property value
            result = prop.GetValue(Instance, index: null);

            // Wrap the sub object if necessary. This allows nested anonymous objects to work.
            result = result.AsDynamic();

            return true;
        }

        public override bool TrySetMember(SetMemberBinder binder, object value) {
            IProperty prop = GetProperty(binder.Name);

            // Set the property value.  Make sure to unwrap it first if it's one of our dynamic objects
            prop.SetValue(Instance, Unwrap(value), index: null);

            return true;
        }

        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object result) {
            // The indexed property is always named "Item" in C#
            IProperty prop = GetIndexProperty();
            result = prop.GetValue(Instance, indexes);

            // Wrap the sub object if necessary. This allows nested anonymous objects to work.
            result = result.AsDynamic();

            return true;
        }

        public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object value) {
            // The indexed property is always named "Item" in C#
            IProperty prop = GetIndexProperty();
            prop.SetValue(Instance, Unwrap(value), indexes);
            return true;
        }

        // Called when a method is called
        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result) {
            result = InvokeMemberOnType(TargetType, Instance, binder.Name, args);

            // Wrap the sub object if necessary. This allows nested anonymous objects to work.
            result = result.AsDynamic();

            return true;
        }

        public override bool TryConvert(ConvertBinder binder, out object result) {
            result = Convert.ChangeType(Instance, binder.Type);
            return true;
        }

        public override string ToString() {
            return Instance.ToString();
        }

        private IProperty GetIndexProperty() {
            // The index property is always named "Item" in C#
            return GetProperty("Item");
        }

        private IProperty GetProperty(string propertyName) {

            // Get the list of properties and fields for this type
            IDictionary<string, IProperty> typeProperties = GetTypeProperties(TargetType);

            // Look for the one we want
            IProperty property;
            if (typeProperties.TryGetValue(propertyName, out property)) {
                return property;
            }

            // The property doesn't exist

            // Get a list of supported properties and fields and show them as part of the exception message
            // For fields, skip the auto property backing fields (which name start with <)
            var propNames = typeProperties.Keys.Where(name => name[0] != '<').OrderBy(name => name);
            throw new ArgumentException(
                String.Format(
                "The property {0} doesn't exist on type {1}. Supported properties are: {2}",
                propertyName, TargetType, String.Join(", ", propNames)));
        }

        private IDictionary<string, IProperty> GetTypeProperties(Type type) {
            // First, check if we already have it cached
            IDictionary<string, IProperty> typeProperties;
            if (PropertiesOnType.TryGetValue(type, out typeProperties)) {
                return typeProperties;
            }

            // Not cached, so we need to build it

            typeProperties = new ConcurrentDictionary<string, IProperty>();

            // First, recurse on the base class to add its fields
            if (type.BaseType != null) {
                foreach (IProperty prop in GetTypeProperties(type.BaseType).Values) {
                    typeProperties[prop.Name] = prop;
                }
            }

            // Then, add all the properties from the current type
            foreach (PropertyInfo prop in type.GetProperties(BindingFlags).Where(p => p.DeclaringType == type)) {
                typeProperties[prop.Name] = new Property { PropertyInfo = prop };
            }

            // Finally, add all the fields from the current type
            foreach (FieldInfo field in type.GetFields(BindingFlags).Where(p => p.DeclaringType == type)) {
                typeProperties[field.Name] = new Field { FieldInfo = field };
            }

            // Cache it for next time
            PropertiesOnType[type] = typeProperties;

            return typeProperties;
        }

        private object InvokeMemberOnType(Type type, object target, string name, object[] args) {

            // If any of the arguments are our wrapped objects, use the real object/type instead
            var convertedArgs = args.Select(Unwrap).ToArray();

            try {
                // Try to invoke the method
                return type.InvokeMember(
                    name,
                    BindingFlags.InvokeMethod | BindingFlags,
                    null,
                    target,
                    convertedArgs);
            }
            catch (MissingMethodException) {
                // If we couldn't find the method, try on the base class
                if (type.BaseType != null) {
                    return InvokeMemberOnType(type.BaseType, target, name, args);
                }

                throw;
            }
        }

        private static object Unwrap(object o) {
            var dynObject = o as PrivateReflectionDynamicObjectBase;

            // If it's a wrap object, unwrap it and return the real thing
            if (dynObject != null)
                return dynObject.RealObject;

            // Otherwise, return it unchanged
            return o;
        }
    }
}