﻿using System;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;
using LyraWallet.Views;
using LyraWallet.Services;
using System.IO;
using LyraWallet.Models;
using ReduxSimple;
using LyraWallet.States;

[assembly: XamlCompilation(XamlCompilationOptions.Compile)]
namespace LyraWallet
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; set; }

        public static readonly ReduxStore<RootState> Store =
            new ReduxStore<RootState>(States.Reducers.CreateReducers(), RootState.InitialState, true);

        public static WalletContainer Container;

        public App()
        {
            Container = new WalletContainer();

            InitializeComponent();

            MainPage = new AppShell();
        }

        protected override void OnStart()
        {
            Store.RegisterEffects(LyraWallet.States.Effects.CreateWalletEffect);
        }

        protected override void OnSleep()
        {
            // close wallet
            //Container.CloseWallet();
        }

        protected override void OnResume()
        {
            // re-open wallet
            //Container.OpenWalletFileAsync();
        }
    }
}
