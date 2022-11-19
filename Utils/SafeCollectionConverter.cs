using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Utils
{
    public class SafeNullConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return true;
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            //This not works for Populate (on existingValue)
            return serializer.Deserialize<JToken>(reader).ToObjectNullSafe(objectType, serializer);
        }

        public override bool CanWrite => false;

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }
    /**
     * From https://stackoverflow.com/questions/27047875/json-net-deserialization-single-result-vs-array
     */
    public class SafeCollectionConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return true;
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            //This not works for Populate (on existingValue)
            return serializer.Deserialize<JToken>(reader).ToObjectCollectionSafe(objectType, serializer);
        }

        public override bool CanWrite => false;

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }
    public static class SafeJsonConvertExtensions
    {
        public static object ToObjectNullSafe(this JToken jtoken, Type objectType, JsonSerializer jsonSerializer)
        {
            if(jtoken is JValue && ((JValue)jtoken).Value.ToString() == "null")
            {
                return Activator.CreateInstance(objectType);
            }
            return jtoken.ToObject(objectType, jsonSerializer);
        }

        public static object ToObjectCollectionSafe(this JToken jToken, Type objectType, JsonSerializer jsonSerializer)
        {
            var expectArray = typeof(System.Collections.IEnumerable).IsAssignableFrom(objectType) && !typeof(string).IsAssignableFrom(objectType);

            if (jToken is JArray jArray)
            {
                if (!expectArray)
                {
                    //to object via singel
                    if (jArray.Count == 0)
                        return JValue.CreateNull().ToObject(objectType, jsonSerializer);

                    if (jArray.Count == 1)
                        return jArray.First.ToObject(objectType, jsonSerializer);
                }
            }
            else if (expectArray)
            {
                //to object via JArray
                return new JArray(jToken).ToObject(objectType, jsonSerializer);
            }

            return jToken.ToObject(objectType, jsonSerializer);
        }

        public static object ToObjectCollectionSafe(this JToken jToken, Type objectType)
        {
            return ToObjectCollectionSafe(jToken, objectType, JsonSerializer.CreateDefault());
        }
        public static T ToObjectCollectionSafe<T>(this JToken jToken)
        {
            return (T)ToObjectCollectionSafe(jToken, typeof(T));
        }
        public static T ToObjectCollectionSafe<T>(this JToken jToken, JsonSerializer jsonSerializer)
        {
            return (T)ToObjectCollectionSafe(jToken, typeof(T), jsonSerializer);
        }
    }
}
