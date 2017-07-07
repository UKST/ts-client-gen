﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Http;
using Newtonsoft.Json;

namespace TSClientGen
{
	static class TSGenerator
	{
		public static void GenerateControllerClient(this StringBuilder result, Type controller, TypeMapper mapper, RouteAttribute controllerRoute)
		{
			var routePrefix = controller.GetCustomAttributes<RoutePrefixAttribute>().SingleOrDefault()?.Prefix;
			routePrefix = string.IsNullOrEmpty(routePrefix) ? "/" : "/" + routePrefix + "/";

			result.AppendLine($"export class {controller.Name.Replace("Controller", "")}Client {{");

			var actions = controller.GetMethods(BindingFlags.Instance | BindingFlags.Public).ToArray();
			foreach (var action in actions)
			{
				var descriptor = ActionDescriptor.TryCreateFrom(action, controllerRoute);
				if (descriptor == null)
					continue;
		
				if (descriptor.GenerateUrl)
					generateMethod(result, mapper, action, descriptor, routePrefix, true);

				generateMethod(result, mapper, action, descriptor, routePrefix, false);
			}

			result.AppendLine("}");
			result.AppendLine();
		}

		public static void GenerateJsonModule(this StringBuilder result, Type controller, TypeMapper mapper, RouteAttribute controllerRoute)
		{
			var actions = controller.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(a => ActionDescriptor.TryCreateFrom(a, controllerRoute) != null).ToArray();
			if (actions.Length != 1)
				throw new Exception("Only one action allowed in controller marked as json module for TypeScript");

			var action = actions.Single();
			if (action.GetParameters().Any())
				throw new Exception("Action should not have any parameters when controller marked as json module for TypeScript");

			result.AppendLine($"declare var result: {mapper.GetTSType(action.ReturnType)}");
			result.AppendLine($"export = result");
		}

		public static void GenerateStaticContent(this StringBuilder result, TypeScriptStaticContentAttribute staticContentModule)
		{
			foreach (var entry in staticContentModule.Content)
			{
				result.AppendLine($"export let {entry.Key} = {JsonConvert.SerializeObject(entry.Value)};");
			}
		}

		public static void GenerateEnums(
			this StringBuilder sb,
			IEnumerable<Type> enumTypes,
			ILookup<Type, TypeScriptExtendEnumAttribute> staticMemberProvidersByEnum,
			TypeMapper mapper)
		{
			bool requireResourceImport = false;

			foreach (var @enum in enumTypes)
			{
				sb.GenerateEnum(@enum, mapper);

				if (!staticMemberProvidersByEnum[@enum].Any())
					continue;

				sb.AppendLine($"export namespace {@enum.Name} {{");

				foreach (var provider in staticMemberProvidersByEnum[@enum])
				{
					provider.GenerateStaticMembers(sb);
				}

				sb.AppendLine("}");

				requireResourceImport = true;
			}

			if (requireResourceImport)
			{
				sb.AppendLine();
				sb.AppendLine("import * as getResource from 'resource!global/enums'");
			}
		}

		public static void GenerateEnum(this StringBuilder sb, Type enumType, TypeMapper mapper)
		{
			var names = Enum.GetNames(enumType);
			var underlyingType = Enum.GetUnderlyingType(enumType);

			sb.AppendLine($"export enum {mapper.GetTSType(enumType)} {{");

			foreach (string name in names)
			{
				var value = Convert.ChangeType(Enum.Parse(enumType, name), underlyingType);
				sb.AppendLine($"\t{name} = {value},");
			}

			sb.AppendLine("}");
			sb.AppendLine();
		}

		public static void GenerateEnumLocalizations(this StringBuilder sb, IReadOnlyCollection<TypeScriptEnumLocalizationAttribute> enumLocalizations)
		{
			foreach (var enumLocalization in enumLocalizations)
			{
				var prefix = enumLocalization.EnumType.Name;

				foreach (var name in Enum.GetNames(enumLocalization.EnumType))
				{
					var resourceKey = enumLocalization.UsePrefix ? $"{prefix}_{name}" : name;
					var localization = enumLocalization.ResourceManager.GetString(resourceKey);
					if (localization == null)
					{
						throw new Exception($"Enum value {enumLocalization.EnumType.Name}.{name} is not localized in RESX {enumLocalization.ResxName}");
					}

					sb.Append($"msgid \"{prefix}_{name}\"");
					sb.AppendLine();
					sb.Append($"msgstr \"{localization}\"");
					sb.AppendLine();
					sb.AppendLine();
				}
			}
		}

		public static void GenerateInterface(this StringBuilder sb, Type modelType, TypeMapper mapper, bool export)
		{
			if (export)
				sb.Append("export ");

			sb.Append($"interface {mapper.GetTSType(modelType)} ");
			if (modelType.BaseType != null && mapper.Contains(modelType.BaseType))
			{
				sb.Append($"extends {mapper.GetTSType(modelType.BaseType)} ");
			}
			sb.AppendLine("{");

			foreach (var property in modelType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
			{
				var ignoreDataMember = property.GetCustomAttributes<IgnoreDataMemberAttribute>().FirstOrDefault();
				if (ignoreDataMember != null)
					continue;

				var dataMember = property.GetCustomAttributes<DataMemberAttribute>().FirstOrDefault();
				var name = dataMember != null ? dataMember.Name : property.Name.toLowerCamelCase();
				if (Nullable.GetUnderlyingType(property.PropertyType) != null)
					name += "?";

				var typeAttribute = property.GetCustomAttributes<TypeScriptTypeAttribute>().FirstOrDefault();
				var propertyType = (typeAttribute != null)
					? ((typeAttribute.SubstituteType != null)
						? mapper.GetTSType(typeAttribute.SubstituteType)
						: typeAttribute.TypeDefinition)
					: mapper.GetTSType(property.PropertyType);

				sb.AppendLine($"\t{name}: {propertyType};");
			}

			sb.AppendLine("}");
			sb.AppendLine("");
		}

		public static void GenerateTypeDefinition(this StringBuilder sb, Type modelType, string typeDefinition, TypeMapper mapper, bool export)
		{
			if (export)
				sb.Append("export ");

			sb.AppendLine($"type {mapper.GetTSType(modelType)} = {typeDefinition}");
			sb.AppendLine("");
		}

		private static void generateMethod(
			StringBuilder result,
			TypeMapper mapper,
			MethodInfo action,
			ActionDescriptor descriptor,
			string routePrefix,
			bool generateGetUrl)
		{
			result.Append(generateGetUrl 
				? $"\t{action.Name.toLowerCamelCase()}Url("
				: $"\t{action.Name.toLowerCamelCase()}(");

			var parameters = descriptor.AllParams
				.Where(param => !descriptor.IsUploadedFile || param != descriptor.BodyParam)
				.Select(param => $"{param.Name}{(param.IsOptional ? "?" : "")}: {mapper.GetTSType(param.ParameterType)}")
				.ToList();
			if (descriptor.IsModelWithFiles || descriptor.IsUploadedFile)
				parameters.Add("files: File[]");
			if (!generateGetUrl)
				parameters.Add("suppressAjaxError?: boolean");

			result.Append(string.Join(", ", parameters));
			result.AppendLine(generateGetUrl
				? $"): string {{"
				: $"): Promise<{mapper.GetTSType(action.ReturnType)}> {{");

			generateMethodBody(result, routePrefix, descriptor, action, generateGetUrl);
			result.AppendLine("\t}");
			result.AppendLine();
		}

		private static void generateMethodBody(
			this StringBuilder result, 
			string routePrefix, 
			ActionDescriptor actionDescriptor, 
			MethodInfo action,
			bool generateGetUrl)
		{
			result.AppendLine($"\t\tvar url = '{routePrefix}{actionDescriptor.RouteTemplate}';");

			foreach (var param in actionDescriptor.RouteParamsBySections)
			{
				result.AppendLine($"\t\turl = url.replace('{param.Key}', {param.Value.Name}.toString());");
			}

			if (actionDescriptor.QueryParams.Any())
			{
				result.AppendLine("\t\tvar queryParams: any = {};");

				foreach (var param in actionDescriptor.QueryParams)
				{
					result.AppendLine($"\t\tif (typeof {param.Name} != 'undefined') {{");
					result.AppendLine($"\t\t\tqueryParams.{param.Name} = {param.Name};");
					result.AppendLine($"\t\t}}");
				}

				result.AppendLine($"\t\tvar queryString = $.param(queryParams);");
				result.AppendLine($"\t\tif (queryString) {{");
				result.AppendLine("\t\t\turl = url + '?' + queryString;");
				result.AppendLine("\t\t}");
			}

			if (generateGetUrl)
			{
				result.AppendLine("\t\treturn url;");
				return;
			}

			if (actionDescriptor.IsModelWithFiles || actionDescriptor.IsUploadedFile)
			{
				result.AppendLine("\t\tvar formData = new FormData();");
				result.AppendLine("\t\tfor (let f of files) {");
				result.AppendLine("\t\t\tformData.append('file', f);");
				result.AppendLine("\t\t}");

				if (actionDescriptor.IsModelWithFiles)
				{
					result.AppendLine($"\t\tvar blob = new Blob([JSON.stringify({actionDescriptor.BodyParam.Name})], {{ type: 'application/json' }});");
					result.AppendLine("\t\tformData.append('Value', blob);");
				}
			}

			result.AppendLine("\t\treturn ajax({");
			result.AppendLine($"\t\t\turl: url,");
			result.AppendLine("\t\t\terror: (jqXhr: JQueryXHR) => { jqXhr.suppressAjaxError = suppressAjaxError; },");
			result.AppendLine($"\t\t\ttype: '{actionDescriptor.HttpVerb}',");
			if (actionDescriptor.BodyParam != null)
			{
				if (actionDescriptor.IsModelWithFiles || actionDescriptor.IsUploadedFile)
				{
					if (action.ReturnType != typeof(void))
					{
						result.AppendLine("\t\t\tparseResponseAsJson: true,");
					}
					result.AppendLine("\t\t\tcontentType: false,");
					result.AppendLine("\t\t\tprocessData: false,");
					result.AppendLine($"\t\t\tdata: formData");
				}
				else
				{
					result.AppendLine("\t\t\tcontentType: 'application/json',");
					result.AppendLine($"\t\t\tdata: JSON.stringify({actionDescriptor.BodyParam.Name})");
				}
				
			}
			result.AppendLine("\t\t});");
		}

		private static string toLowerCamelCase(this string name)
		{
			return char.ToLowerInvariant(name[0]) + name.Substring(1);
		}
	}
}