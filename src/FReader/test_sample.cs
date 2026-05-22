using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TestApp
{
    class TestClass<T>
    {
        public async Task<Dictionary<string, List<T>>> GetDataAsync(int id)
        {
            var result = new Dictionary<string, List<T>>();
            return result;
        }

        public void Save(string key, T value) { }

        public int Count { get; set; }

        public override string ToString() => "test";
        public override bool Equals(object? obj) => false;
        public override int GetHashCode() => 0;
    }
}
