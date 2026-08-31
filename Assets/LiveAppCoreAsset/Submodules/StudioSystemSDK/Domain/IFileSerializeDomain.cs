using UnityEngine;

namespace StudioSystemSDK.Domain
{
    /// <summary>
    /// Json Serialize 처리 관련 Interface
    /// </summary>
    public interface IFileSerializeDomain
    {
        /// <summary>
        /// Binary 파알로 변환
        /// </summary>
        /// <param name="rawMessage">평문 데이터</param>
        /// <returns></returns>
        string SerializeToBinary( string rawMessage );
        /// <summary>
        /// Json을 T Type 데이터 Class로 변환
        /// </summary>
        /// <typeparam name="T">Data Type</typeparam>
        /// <param name="rawMessage">Json Data</param>
        /// <returns>Data Type</returns>
        T DeserializeFromJson<T>( string rawMessage );
    }
}
