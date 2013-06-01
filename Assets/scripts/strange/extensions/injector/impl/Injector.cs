/**
 * Extension which uses System.Reflection to satisfy injection dependencies.
 * 
 * Dependencies may be Constructor injections (all parameters will be satisfied),
 * or setter injections.
 * 
 * Classes utilizing this injector must be marked with the following metatags:
 * - [Inject]
 * Use this metatag on any setter you wish to have supplied by injection
 * - [Construct]
 * Use this metatag on the specific Constructor you wish to inject into when using Constructor injection
 * - [PostConstruct]
 * Use this metatag on any method(s) you wish to fire directly after dependencies are supplied
 * 
 * TODO: Reflection is innately inefficient. Can improve performace by caching class mappings
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using strange.framework.api;
using strange.extensions.injector.api;
using strange.extensions.reflector.api;

namespace strange.extensions.injector.impl
{
	public class Injector : IInjector
	{
		private Dictionary<IInjectionBinding, int> infinityLock;
		private int INFINITY_LIMIT = 10;
		
		public Injector ()
		{
			factory = new InjectorFactory();
		}

		public IInjectorFactory factory{ get; set;}
		public IInjectionBinder binder{ get; set;}
		public IReflectionBinder reflector{ get; set;}

		public object Instantiate(IInjectionBinding binding)
		{
			failIf(binder == null, "Attempt to instantiate from Injector without a Binder", InjectionExceptionType.NO_BINDER);
			failIf(factory == null, "Attempt to inject into Injector without a Factory", InjectionExceptionType.NO_FACTORY);

			armorAgainstInfiniteLoops (binding);

			object retv = factory.Get (binding);

			//Factory can return null in the case that there are no parameterless constructors.
			//In this case, the following routine attempts to generate based on a preferred constructor
			if (retv == null)
			{
				IReflectedClass reflection = reflector.Get (binding.value as Type);
				Type[] parameters = reflection.constructorParameters;
				int aa = parameters.Length;
				object[] args = new object [aa];
				for (int a = 0; a < aa; a++)
				{
					args [a] = getValueInjection (parameters[a] as Type, null);
				}
				retv = factory.Get (binding, args);
				if (retv == null)
				{
					return null;
				}
				retv = Inject (retv, false);
			}
			else if (binding.toInject)
			{
				retv = Inject (retv, true);
				if (binding.type == InjectionBindingType.SINGLETON || binding.type == InjectionBindingType.VALUE)
				{
					//prevent double-injection
					binding.ToInject(false);
				}
			}
			infinityLock = null;

			return retv;
		}

		public object Inject(object target)
		{
			return Inject (target, true);
		}

		protected object Inject(object target, bool attemptConstructorInjection)
		{
			failIf(binder == null, "Attempt to inject into Injector without a Binder", InjectionExceptionType.NO_BINDER);
			failIf(reflector == null, "Attempt to inject without a reflector", InjectionExceptionType.NO_REFLECTOR);
			failIf(target == null, "Attempt to inject into null instance", InjectionExceptionType.NULL_TARGET);

			//Some things can't be injected into. Bail out.
			Type t = target.GetType ();
			if (t.IsPrimitive || t == typeof(Decimal) || t == typeof(string))
			{
				return target;
			}

			IReflectedClass reflection = reflector.Get (t);

			if (attemptConstructorInjection)
			{
				target = performConstructorInjection(target, reflection);
			}
			performSetterInjection(target, reflection);
			postInject(target, reflection);
			return target;
		}

		private object performConstructorInjection(object target, IReflectedClass reflection)
		{
			failIf(target == null, "Attempt to perform constructor injection into a null object", InjectionExceptionType.NULL_TARGET);
			failIf(reflection == null, "Attempt to perform constructor injection without a reflection", InjectionExceptionType.NULL_REFLECTION);

			ConstructorInfo constructor = reflection.constructor;
			failIf(constructor == null, "Attempt to construction inject a null constructor", InjectionExceptionType.NULL_CONSTRUCTOR);

			Type[] constructorParameters = reflection.constructorParameters;
			object[] values = new object[constructorParameters.Length];
			int i = 0;
			foreach (Type type in constructorParameters)
			{
				values[i] = getValueInjection(type, null);
				i++;
			}
			if (values.Length == 0)
			{
				return target;
			}

			object constructedObj = constructor.Invoke (values);
			return (constructedObj == null) ? target : constructedObj;
		}

		private void performSetterInjection(object target, IReflectedClass reflection)
		{
			failIf(target == null, "Attempt to inject into a null object", InjectionExceptionType.NULL_TARGET);
			failIf(reflection == null, "Attempt to inject without a reflection", InjectionExceptionType.NULL_REFLECTION);
			failIf(reflection.setters.Length != reflection.setterNames.Length, "Attempt to perform setter injection with mismatched names.\nThere must be exactly as many names as setters.", InjectionExceptionType.SETTER_NAME_MISMATCH);

			int aa = reflection.setters.Length;
			for(int a = 0; a < aa; a++)
			{
				KeyValuePair<Type, PropertyInfo> pair = reflection.setters [a];
				object value = getValueInjection(pair.Key, reflection.setterNames[a]);
				injectValueIntoPoint (value, target, pair.Value);
			}
		}

		private object getValueInjection(Type t, object name)
		{
			IInjectionBinding binding = binder.GetBinding (t, name);
			failIf(binding == null, "Attempt to Instantiate a null binding.", InjectionExceptionType.NULL_BINDING, t, name);
			return Instantiate (binding);
		}

		//Inject the value into the target at the specified injection point
		private void injectValueIntoPoint(object value, object target, PropertyInfo point)
		{
			failIf(target == null, "Attempt to inject into a null target", InjectionExceptionType.NULL_TARGET);
			failIf(point == null, "Attempt to inject into a null point", InjectionExceptionType.NULL_INJECTION_POINT);
			failIf(value == null, "Attempt to inject null into a target object", InjectionExceptionType.NULL_VALUE_INJECTION);

			point.SetValue (target, value, null);
		}

		//After injection, call any methods labelled with the [PostConstruct] tag
		private void postInject(object target, IReflectedClass reflection)
		{
			failIf(target == null, "Attempt to PostConstruct a null target", InjectionExceptionType.NULL_TARGET);
			failIf(reflection == null, "Attempt to PostConstruct without a reflection", InjectionExceptionType.NULL_REFLECTION);

			MethodInfo[] postConstructors = reflection.postConstructors;
			if (postConstructors != null)
			{
				foreach(MethodInfo method in postConstructors)
				{
					method.Invoke (target, null);
				}
			}
		}

		private void failIf(bool condition, string message, InjectionExceptionType type)
		{
			if (condition)
			{
				throw new InjectionException(message, type);
			}
		}

		private void failIf(bool condition, string message, InjectionExceptionType type, Type t, object name)
		{
			if (condition)
			{
				message += "\n\t\ttype: " + t;
				message += "\n\t\tname: " + name;
				throw new InjectionException(message, type);
			}
		}

		private void armorAgainstInfiniteLoops(IInjectionBinding binding)
		{
			if (binding == null)
			{
				return;
			}
			if (infinityLock == null)
			{
				infinityLock = new Dictionary<IInjectionBinding, int> ();
			}
			if(infinityLock.ContainsKey(binding) == false)
			{
				infinityLock.Add (binding, 0);
			}
			infinityLock [binding] = infinityLock [binding] + 1;
			if (infinityLock [binding] > INFINITY_LIMIT)
			{
				throw new InjectionException ("There appears to be a circular dependency. Terminating loop.", InjectionExceptionType.CIRCULAR_DEPENDENCY);
			}
		}
	}
}

