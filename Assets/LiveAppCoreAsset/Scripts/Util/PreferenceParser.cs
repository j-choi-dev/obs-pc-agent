using Cysharp.Threading.Tasks;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using Application = UnityEngine.Application;
using System;
using UniRx;
using UnityEngine;

namespace LiveAppCore.Preference
{
    public class PreferenceParser
    {
        private object lockObject = new object();

        private Subject<string> m_OnPreferenceChanged = new Subject<string>();
        public IObservable<string> ObserveChangedPreferenceKey => m_OnPreferenceChanged;

        private string m_Path;
        private Dictionary<string, object> m_PrefValues = new Dictionary<string, object>();
        private Dictionary<string, object> PrefValues
        {
            get
            {
                lock( lockObject )
                {
                    return m_PrefValues;
                }
            }
        }

        public PreferenceParser( string fileName )
        {
            if( string.IsNullOrEmpty( Path.GetExtension( fileName ) ) )
            {
                fileName = Path.ChangeExtension( fileName, ".json" );
            }
            m_Path = Path.Combine( Application.persistentDataPath, fileName );

            Initialize();
        }

        private void Initialize()
        {
            lock( lockObject )
            {
                try
                {
                    var json = File.ReadAllText( m_Path );
                    var dir = MiniJSON.Json.Deserialize( json ) as IDictionary;
                    m_PrefValues.Clear();
                    foreach( string key in dir.Keys )
                    {
                        m_PrefValues[key] = dir[key];
                        m_OnPreferenceChanged.OnNext( key );
                    }
                }
                catch( Exception e )
                {
                    m_PrefValues.Clear();
                    Debug.LogError( $"ReadFile Failed: {e.Message}" );
                    Debug.LogError( $"m_PrefValues was set to defaultValue." );
                }
            }
        }

        public UniTask SaveInt( string key, int value )
        {
            SetValue( key, value );
            return WriteFile();
        }
        public UniTask SaveFloat( string key, float value )
        {
            SetValue( key, value );
            return WriteFile();
        }
        public UniTask SaveString( string key, string value )
        {
            SetValue( key, value );
            return WriteFile();
        }
        public UniTask SaveBool( string key, bool value )
        {
            SetValue( key, value );
            return WriteFile();
        }
        private void SetValue( string key, object obj )
        {
            lock( lockObject )
            {
                m_PrefValues[key] = obj;
            }
            m_OnPreferenceChanged.OnNext( key );
        }

        public int GetInt( string key, int defaultValue )
        {
            return ( int )GetValue<long>( key, defaultValue );
        }
        public float GetFloat( string key, float defaultValue )
        {
            return ( float )GetValue<double>( key, defaultValue );
        }
        public string GetString( string key, string defaultValue )
        {
            return GetValue( key, defaultValue );
        }
        public bool GetBool( string key, bool defaultValue )
        {
            return GetValue( key, defaultValue );
        }
        private T GetValue<T>( string key, T defaultValue )
        {
            try
            {
                if( PrefValues.ContainsKey( key ) )
                {
                    return ( T )m_PrefValues[key];
                }
                else
                {
                    return defaultValue;
                }
            }
            catch( InvalidCastException )
            {
                DeleteKey( key ).Forget();
                return defaultValue;
            }
        }

        public bool IsKey( string key )
        {
            return m_PrefValues.ContainsKey( key );
        }

        public UniTask DeleteKey( string key )
        {
            if( m_PrefValues.ContainsKey( key ) )
            {
                m_PrefValues.Remove( key );
                return WriteFile();
            }
            return UniTask.CompletedTask;
        }

        private async UniTask WriteFile()
        {
            await UniTask.SwitchToThreadPool();

            try
            {
                lock( lockObject )
                {
                    var json = MiniJSON.Json.Serialize( m_PrefValues );
                    File.WriteAllText( m_Path, json );

                }
            }
            finally
            {
                await UniTask.SwitchToMainThread();
            }
        }
    }
}
